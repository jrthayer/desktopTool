using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Readme.UI;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.ClaudePipeline.UI;

/// <summary>
/// "Claude Toolbox" widget - lists every user-defined PipelineFeature as a row with an on/off
/// switch (see ClaudePipelineManager.SetEnabled/ClaudeSettingsSync), rebuilt on LayeredWidgetForm the
/// same way LayoutLauncherWidget/WidgetManagerWidget are. A "Manage Features..." row pinned to the
/// body's bottom edge (see GetContentButtons) opens the editor for adding/editing/deleting features;
/// clicking a row's own name does the same, jumped straight to that feature (see GetRowActionAt/
/// FireRowAction); clicking its switch toggles it on/off directly, without opening the editor at all;
/// clicking its info icon (only present for a feature with a Description) opens a read-only info
/// window instead (see OpenFeatureInfo) - a generalized ReadmeWidget, not a tooltip, since a
/// Description can run far longer than this widget's own small bounds could ever show one in.
/// Everything not genuinely specific to this widget (theme derivation, the Settings dropdown's default
/// rows, button/border/title/list painting) is LayeredWidgetForm's own - see its own class comment.
/// </summary>
internal sealed class ClaudePipelineWidget : LayeredWidgetForm
{
    private const int OuterMarginPx = 13;
    private const int HeaderHeight = 28;
    private const int ButtonBandOverhang = 19;
    private const int TopMarginWithButtons = OuterMarginPx + ButtonBandOverhang;
    private const int DefaultBodyHeight = 120;

    private readonly ClaudePipelineManager _manager;
    private readonly ClaudePipelineModel _model;
    private readonly ClaudePipelineWidgetStore _store;

    private bool _allowClose;
    private bool _settingsButtonArmed;

    // Row click handling - clicking a row's own name opens the editor jumped to that feature; its
    // switch toggles Enabled directly instead; its info icon (when the feature has a Description)
    // opens a read-only info window instead of either. Same arm-then-fire pattern as every other
    // button on this base - see LayoutLauncherWidget's own RowAction for the precedent.
    private enum RowAction { None, Edit, Toggle, Info }
    private RowAction _armedRowAction = RowAction.None;
    private int _armedRowIndex = -1;

    private readonly PaintedTooltip _rowTooltip = new();

    // Reused/refreshed rather than reopened - same "create once, activate/refresh the existing one"
    // idea as TrayApplicationContext's own _layoutEditor/_pipelineEditor, just owned locally here
    // since this widget already has everything (ClaudePipelineManager, Fences) a ReadmeWidget needs
    // and there's no reason to route it through the tray. Genuinely closable/reopenable though (see
    // ReadmeWidget's own ephemeral lifecycle) - null until first opened, nulled again on close.
    private ReadmeWidget? _featureInfoWidget;

    protected override IReadOnlyList<ChromeButton> ExtraButtons { get; }

    /// <summary>Guid? jumps a freshly opened (or already-open) editor straight to that feature - null
    /// from the "Manage Features..." row itself (there's no specific feature to jump to), same
    /// convention as LayoutLauncherWidget.ManageLayoutsRequested.</summary>
    public event EventHandler<Guid?>? ManageFeaturesRequested;

    /// <summary>Fired whenever ShowAndPersist/HideAndPersist actually change Visible - lets
    /// WidgetManagerWidget repaint its own Claude Pipeline row immediately, same reasoning as
    /// LayoutLauncherWidget.VisibilityChanged.</summary>
    public event EventHandler? VisibilityChanged;

    protected override int OuterMargin => OuterMarginPx;
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMarginWithButtons;
    protected override int BottomBand => ButtonRowAtBottom ? TopMarginWithButtons : OuterMargin;
    protected override int MaxTopBand => TopMarginWithButtons;

    protected override IWidgetStyle Style => _model;

    public ClaudePipelineWidget(ClaudePipelineManager manager, FenceManager fenceManager, ClaudePipelineModel model, ClaudePipelineWidgetStore store)
        : base(model.Opacity / 100f, fenceManager)
    {
        _manager = manager;
        _model = model;
        _store = store;

        _manager.FeaturesChanged += OnFeaturesChanged;
        _manager.SyncFailed += OnSyncFailed;

        ExtraButtons = new List<ChromeButton>
        {
            new("×", 22, HideAndPersist, "Hide Claude Toolbox"),
        };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = AppTheme.Font;

        RenderAndPresent();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_model is null)
                return cp;

            var bodyX = _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2;
            var bodyY = _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - DefaultBodyHeight) / 2;
            var bodyHeight = _model.Height ?? DefaultBodyHeight;

            ButtonRowAtBottom = ComputeButtonRowAtBottom(new Point(bodyX, bodyY), TopMarginWithButtons);

            cp.Width = _model.Width + OuterMargin * 2;
            cp.Height = bodyHeight + TopBand + BottomBand;
            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_CLIPCHILDREN;
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
            cp.X = bodyX - OuterMargin;
            cp.Y = bodyY - TopBand;
            return cp;
        }
    }

    public void ToggleVisible()
    {
        if (Visible)
            HideAndPersist();
        else
            ShowAndPersist();
    }

    private void ShowAndPersist()
    {
        Show();
        Activate();
        _model.Visible = true;
        Persist();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HideAndPersist()
    {
        Hide();
        _model.Visible = false;
        Persist();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Shutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing
            or CloseReason.ApplicationExitCall or CloseReason.FormOwnerClosing))
        {
            e.Cancel = true;
            HideAndPersist();
            return;
        }

        base.OnFormClosing(e);
    }

    private void Persist() => _store.Save(_model);

    private void OnFeaturesChanged(object? sender, EventArgs e)
    {
        SyncRowsShownToMax();
        RenderAndPresent();

        // Keeps an already-open info window's own list/selected body text in sync with an edit/toggle
        // made elsewhere (the editor, most commonly) while it happens to be sitting open - same
        // reasoning as ClaudePipelineEditorForm's own FeaturesChanged subscription.
        if (_featureInfoWidget is { IsDisposed: false })
            _featureInfoWidget.RefreshEntries(BuildInfoEntries());
    }

    /// <summary>Surfaces a settings.json read/write failure (see ClaudePipelineManager.SyncFailed) as
    /// a message box - the only feedback the user gets that a toggle/edit/delete didn't actually take,
    /// since ClaudeSettingsSync itself never crashes the app for it.</summary>
    private void OnSyncFailed(object? sender, string message) =>
        MessageBox.Show(this, $"Couldn't update ~/.claude/settings.json:\n\n{message}", "Claude Toolbox",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void DisposeOwnedResources()
    {
        _manager.FeaturesChanged -= OnFeaturesChanged;
        _manager.SyncFailed -= OnSyncFailed;
        _featureInfoWidget?.Dispose();
    }

    /// <summary>Every feature's own Name/Description as a (Title, Body) pair, in the same order
    /// they're listed here - what the info window actually shows (see OpenFeatureInfo). A feature with
    /// a blank Description still gets an entry (there's no reason it'd be missing from the list just
    /// because there's nothing to read yet), just with placeholder body text instead.</summary>
    private List<(string Title, string Body)> BuildInfoEntries() =>
        _manager.Features.Select(f => (f.Name, string.IsNullOrWhiteSpace(f.Description) ? "(No description)" : f.Description)).ToList();

    /// <summary>Opens (or, if already open, refreshes and jumps) the read-only feature info window -
    /// a generalized ReadmeWidget (see its own class comment) rather than a tooltip, so a long
    /// Description has an actual window to live in instead of trying to fit in a hand-painted pill
    /// confined to this widget's own small bounds.</summary>
    private void OpenFeatureInfo(Guid featureId)
    {
        var entries = BuildInfoEntries();
        var selectedIndex = Math.Max(0, _manager.Features.ToList().FindIndex(f => f.Id == featureId));

        if (_featureInfoWidget is { IsDisposed: false })
        {
            _featureInfoWidget.RefreshEntries(entries);
            _featureInfoWidget.SelectEntry(selectedIndex);
            _featureInfoWidget.Activate();
            return;
        }

        _featureInfoWidget = new ReadmeWidget(Fences, "Claude Toolbox Features", entries, selectedIndex);
        _featureInfoWidget.FormClosed += (_, _) => _featureInfoWidget = null;
        _featureInfoWidget.Show();
    }

    protected override Rectangle GetCurrentBody() => new(
        _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2,
        _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - DefaultBodyHeight) / 2,
        _model.Width,
        _model.Height ?? DefaultBodyHeight);

    protected override int SnapMargin => _model.Margin;

    protected override void OnDragEnd()
    {
        if (NativeMethods.GetWindowRect(Handle, out var rect))
        {
            _model.X = rect.Left + OuterMargin;
            _model.Y = rect.Top + TopBand;
            _model.Width = rect.Right - rect.Left - OuterMargin * 2;
            _model.Height = rect.Bottom - rect.Top - TopBand - BottomBand;
            Persist();
            SyncRowsShownToMax();
        }

        RenderOpacity.BeginIfNeeded();
    }

    protected override int HitTest(IntPtr lParam)
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
            return HTCLIENT;

        var windowPoint = ScreenLParamToWindowPoint(lParam, rect);
        int x = windowPoint.X;
        int y = windowPoint.Y;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        var contentWidth = width - OuterMargin * 2;
        var contentPoint = ToContent(windowPoint);
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        if (ShowsButtons && (GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint)
            || TryGetExtraButtonAt(contentWidth, onLeft, contentPoint, out _)))
            return HTCLIENT;

        if (IsOverHeaderCloseButton(contentPoint))
            return HTCLIENT;

        if (ShowsButtons)
        {
            var band = OuterMargin + ResizeMargin;
            var topZone = TopBand + ResizeMargin;
            var bottomZone = BottomBand + ResizeMargin;
            if (x <= band || x >= width - band || y <= topZone || y >= height - bottomZone)
                return HTCAPTION;
        }
        else if (ResizeHitTest(windowPoint, width, height) is int resizeCode)
        {
            return resizeCode;
        }

        if (!_model.HideHeader && y - TopBand <= HeaderHeight)
            return HTBORDER;

        return HTCLIENT;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentWidth = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);

        if (TryArmHeaderCloseButton(contentPoint))
            return;
        if (ShowsButtons && GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint))
        {
            _settingsButtonArmed = true;
            return;
        }
        if (ShowsButtons && TryArmExtraButton(contentPoint))
            return;
        if (TryArmContentButton(contentPoint))
            return;
        if (TryHandleListMouseDown(contentPoint))
            return;

        var (action, index) = GetRowActionAt(contentPoint);
        if (action != RowAction.None)
        {
            _armedRowAction = action;
            _armedRowIndex = index;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateListScrollDrag(ToContent(e.Location));
        UpdateRowTooltips(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_rowTooltip.Hide())
            RenderAndPresent();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        HandleListMouseWheel(e.Delta);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentWidth = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);

        FireArmedHeaderCloseButton(contentPoint);

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            if (ShowsButtons && GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint))
                OpenSettingsMenu();
            return;
        }

        FireArmedExtraButton(contentPoint);
        FireArmedContentButton(contentPoint);
        EndListScrollDrag();

        if (_armedRowAction != RowAction.None)
        {
            var armedAction = _armedRowAction;
            var armedIndex = _armedRowIndex;
            _armedRowAction = RowAction.None;
            _armedRowIndex = -1;

            var (action, index) = GetRowActionAt(contentPoint);
            if (action == armedAction && index == armedIndex)
                FireRowAction(action, index);
        }
    }

    protected override string Title
    {
        get => _model.Title;
        set
        {
            _model.Title = value;
            Persist();
        }
    }

    protected override int TitleRowHeight => HeaderHeight;

    protected override bool HideHeader
    {
        get => _model.HideHeader;
        set
        {
            _model.HideHeader = value;
            Persist();
            RenderAndPresent();
        }
    }

    protected override bool ShowHeaderCloseButton
    {
        get => _model.HeaderCloseButton;
        set
        {
            _model.HeaderCloseButton = value;
            Persist();
            RenderAndPresent();
        }
    }

    protected override void PersistStyle() => Persist();

    protected override void CopyAdditionalSettingsFrom(LayeredWidgetForm source)
    {
        if (source is not ClaudePipelineWidget other)
            return;
        _model.RowsShown = other._model.RowsShown;
        _model.AlwaysMaxRows = other._model.AlwaysMaxRows;
    }

    private const int CmdToggleAlwaysMaxRows = 1;

    protected override IReadOnlyList<DropdownMenu.Row>? BuildAdditionalSettingsRows()
    {
        var maxRows = Math.Max(1, ListRowCount);
        return new List<DropdownMenu.Row>
        {
            new(0, "Rows Shown", IsHeader: true),
            new(0, string.Empty, IsStepper: true,
                StepperValue: () => _model.RowsShown,
                OnStepperChange: rows => SetRowsShown(Math.Clamp(rows, 1, maxRows)),
                StepperMin: 1, StepperMax: maxRows, StepperStep: 1, StepperSuffix: "",
                IsEnabled: () => !_model.AlwaysMaxRows),
            new(CmdToggleAlwaysMaxRows, "Always Max Rows", HasCheckbox: true,
                IsChecked: () => _model.AlwaysMaxRows,
                Tooltip: "Rows Shown always tracks the current number of features - the list "
                    + "grows/shrinks with it, without resizing the widget itself"),
        };
    }

    protected override void HandleSettingsCommand(int id)
    {
        switch (id)
        {
            case CmdToggleAlwaysMaxRows:
                _model.AlwaysMaxRows = !_model.AlwaysMaxRows;
                Persist();
                SyncRowsShownToMax();
                break;
            default:
                base.HandleSettingsCommand(id);
                break;
        }
    }

    private void SetRowsShown(int rows)
    {
        if (rows == _model.RowsShown)
            return;
        _model.RowsShown = rows;
        Persist();
        SetBodyHeight(HeightForRows(rows));
    }

    private int HeightForRows(int rows) => NonListOverhead(_model.Width) + rows * ListRowHeight;

    private void SyncRowsShownToMax()
    {
        if (!_model.AlwaysMaxRows)
            return;
        _model.RowsShown = Math.Max(1, ListRowCount);
        Persist();
        RenderAndPresent();
    }

    private void SetBodyHeight(int height)
    {
        var bodyX = _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2;
        var bodyY = _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - DefaultBodyHeight) / 2;
        var newHeight = Math.Max(1, height);
        _model.Height = newHeight;
        Persist();

        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, bodyX - OuterMargin, bodyY - TopBand,
            _model.Width + OuterMargin * 2, newHeight + TopBand + BottomBand,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        RenderAndPresent();
    }

    /// <summary>"Manage Features..." sits inside the body itself, pinned to its bottom edge, same
    /// reasoning as LayoutLauncherWidget's own bottom row - this widget's primary "there's nowhere
    /// else to add/edit/delete a feature from" action, not a chrome control.</summary>
    private const int BottomRowHeight = 26;
    private const int BottomRowGap = 8;
    private const int BottomRowBottomPadding = 12;
    private static readonly int[] BottomRowWidths = { 150 };

    protected override IReadOnlyList<ContentButton> GetContentButtons(int contentWidth, int contentHeight)
    {
        var top = contentHeight - BottomRowBottomPadding - RowHeight(contentWidth, BottomRowHeight, BottomRowGap, BottomRowWidths);
        var rects = LayoutRow(contentWidth, top, BottomRowHeight, BottomRowGap, BottomRowWidths);

        return new[]
        {
            new ContentButton("Manage Features...", rects[0], () => ManageFeaturesRequested?.Invoke(this, null)),
        };
    }

    private const int ListVerticalPadding = 12;
    private const int ListHorizontalPadding = 10;

    private int NonListOverhead(int contentWidth) =>
        (_model.HideHeader ? 0 : HeaderHeight) + ListVerticalPadding * 2
        + BottomRowBottomPadding + RowHeight(contentWidth, BottomRowHeight, BottomRowGap, BottomRowWidths);

    protected override Rectangle GetListArea(int contentWidth, int contentHeight)
    {
        var top = (_model.HideHeader ? 0 : HeaderHeight) + ListVerticalPadding;
        var available = contentHeight - NonListOverhead(contentWidth);
        var wanted = Math.Min(_model.RowsShown, ListRowCount) * ListRowHeight;
        var height = Math.Max(ListRowHeight, Math.Min(available, wanted));
        return new Rectangle(ListHorizontalPadding, top, contentWidth - ListHorizontalPadding * 2, height);
    }

    protected override int ListRowCount => _manager.Features.Count;
    protected override int ListRowHeight => 26;

    private const int SwitchWidth = 38;
    private const int SwitchHeight = 18;
    private const int SwitchRightPadding = 6;

    /// <summary>The row's own switch rect, flush against its right edge - pure relative math off
    /// rowRect's own edges, same convention as WidgetManagerWidget.GetRowSwitchRect.</summary>
    private static Rectangle GetRowSwitchRect(Rectangle rowRect)
    {
        var y = rowRect.Y + (rowRect.Height - SwitchHeight) / 2;
        return new Rectangle(rowRect.Right - SwitchRightPadding - SwitchWidth, y, SwitchWidth, SwitchHeight);
    }

    private const int InfoIconSize = 14;
    private const int InfoIconGap = 6;

    /// <summary>The row's own info icon rect, chained immediately left of the switch - same "chain
    /// rects leftward" convention as WidgetManagerWidget.GetSecondRowButtonRect. Only meaningful (and
    /// only painted/hit-tested) for a row whose feature actually has a Description - see
    /// RowHasDescription.</summary>
    private static Rectangle GetRowInfoIconRect(Rectangle rowRect)
    {
        var switchRect = GetRowSwitchRect(rowRect);
        var y = rowRect.Y + (rowRect.Height - InfoIconSize) / 2;
        return new Rectangle(switchRect.X - InfoIconGap - InfoIconSize, y, InfoIconSize, InfoIconSize);
    }

    private bool RowHasDescription(int index) => !string.IsNullOrWhiteSpace(_manager.Features[index].Description);

    /// <summary>The row's own name-label area - from the row's own left padding up to where the info
    /// icon starts (or the switch, for a row with no Description to show one for), regardless of how
    /// much of it the name text actually fills.</summary>
    private Rectangle GetRowTextArea(Rectangle rowRect, int index)
    {
        var stopX = RowHasDescription(index) ? GetRowInfoIconRect(rowRect).X : GetRowSwitchRect(rowRect).X;
        var width = Math.Max(0, stopX - 4 - (rowRect.X + 8));
        return new Rectangle(rowRect.X + 8, rowRect.Y, width, rowRect.Height);
    }

    private bool TryGetRowAt(Point contentPoint, out int index, out Rectangle rowRect)
    {
        index = -1;
        rowRect = Rectangle.Empty;

        var size = GetContentSize();
        var area = GetListArea(size.Width, size.Height);
        if (area.IsEmpty || !area.Contains(contentPoint))
            return false;

        var candidate = (contentPoint.Y - area.Top + ListScrollOffset) / ListRowHeight;
        if (candidate < 0 || candidate >= ListRowCount)
            return false;

        var maxScroll = Math.Max(0, ListRowCount * ListRowHeight - area.Height);
        var rowWidth = maxScroll > 0 ? area.Width - (Scrollbar.Width + Scrollbar.Margin * 2) : area.Width;
        var rowTop = area.Top + candidate * ListRowHeight - ListScrollOffset;
        var rect = new Rectangle(area.Left, rowTop, rowWidth, ListRowHeight);
        if (!rect.Contains(contentPoint))
            return false;

        index = candidate;
        rowRect = rect;
        return true;
    }

    /// <summary>What clicking contentPoint would do right now - the switch toggles Enabled directly;
    /// the info icon (when present) opens the feature info window; anywhere else in the row opens the
    /// editor jumped to that feature (see FireRowAction).</summary>
    private (RowAction Action, int Index) GetRowActionAt(Point contentPoint)
    {
        if (!TryGetRowAt(contentPoint, out var index, out var rowRect))
            return (RowAction.None, -1);

        if (GetRowSwitchRect(rowRect).Contains(contentPoint))
            return (RowAction.Toggle, index);
        if (RowHasDescription(index) && GetRowInfoIconRect(rowRect).Contains(contentPoint))
            return (RowAction.Info, index);

        return (RowAction.Edit, index);
    }

    private (RowAction Action, int Index, Rectangle TargetRect)? GetRowTooltipTarget(Point contentPoint)
    {
        if (!TryGetRowAt(contentPoint, out var index, out var rowRect))
            return null;

        var switchRect = GetRowSwitchRect(rowRect);
        if (switchRect.Contains(contentPoint))
            return (RowAction.Toggle, index, switchRect);

        if (RowHasDescription(index))
        {
            var infoRect = GetRowInfoIconRect(rowRect);
            if (infoRect.Contains(contentPoint))
                return (RowAction.Info, index, infoRect);
        }

        return (RowAction.Edit, index, GetRowTextArea(rowRect, index));
    }

    private string RowActionTooltipText(RowAction action, int index)
    {
        var feature = _manager.Features[index];
        return action switch
        {
            RowAction.Toggle => feature.Enabled ? $"Turn \"{feature.Name}\" Off" : $"Turn \"{feature.Name}\" On",
            RowAction.Info => $"View \"{feature.Name}\" Description",
            _ => $"Edit \"{feature.Name}\"",
        };
    }

    private void UpdateRowTooltips(Point windowLocation)
    {
        var contentPoint = ToContent(windowLocation);
        var target = GetRowTooltipTarget(contentPoint);

        var changed = target is { } t
            ? _rowTooltip.Show(RowActionTooltipText(t.Action, t.Index), ToWindow(t.TargetRect))
            : _rowTooltip.Hide();

        if (changed)
            RenderAndPresent();
    }

    private void FireRowAction(RowAction action, int index)
    {
        if (index < 0 || index >= _manager.Features.Count)
            return;
        var feature = _manager.Features[index];

        switch (action)
        {
            case RowAction.Toggle:
                _manager.SetEnabled(feature.Id, !feature.Enabled);
                break;
            case RowAction.Edit:
                ManageFeaturesRequested?.Invoke(this, feature.Id);
                break;
            case RowAction.Info:
                OpenFeatureInfo(feature.Id);
                break;
        }
    }

    /// <summary>Name text plus an on/off switch pill at the row's own right edge. Alternates
    /// ThemedListRow/ThemedListRowDark by index so rows read as banded rather than one flat surface,
    /// same as LayoutLauncherWidget/WidgetManagerWidget's own rows.</summary>
    protected override void PaintListRow(Graphics g, int index, Rectangle rowRect)
    {
        var rowBackground = index % 2 == 0 ? ThemedListRow : ThemedListRowDark;
        using (var rowFill = new SolidBrush(rowBackground))
            g.FillRectangle(rowFill, rowRect);

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(Color.WhiteSmoke))
        using (var textFormat = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(_manager.Features[index].Name, Font, textBrush, GetRowTextArea(rowRect, index), textFormat);
        }
        g.TextRenderingHint = previousTextHint;

        if (RowHasDescription(index))
            InfoIcon.Paint(g, GetRowInfoIconRect(rowRect), Color.WhiteSmoke);

        PaintRowSwitch(g, GetRowSwitchRect(rowRect), _manager.Features[index].Enabled, rowBackground);
    }

    /// <summary>Same hand-drawn on/off pill as WidgetManagerWidget.PaintRowSwitch - no existing
    /// toggle-switch control in this app to reuse, see that method's own comment for why.</summary>
    private void PaintRowSwitch(Graphics g, Rectangle rect, bool on, Color rowBackground)
    {
        using var path = RoundedRectPath.Full(rect, rect.Height / 2);
        if (on)
        {
            using var fill = new SolidBrush(Color.White);
            g.FillPath(fill, path);
        }
        else
        {
            using var border = new Pen(ThemedCheckboxBorder);
            g.DrawPath(border, path);
        }

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(on ? rowBackground : Color.WhiteSmoke))
        using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(on ? "On" : "Off", SwitchFont, textBrush, rect, textFormat);
        g.TextRenderingHint = previousTextHint;
    }

    private readonly Font _switchFont = new(AppTheme.Font.FontFamily, 7f);
    private Font SwitchFont => _switchFont;

    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        PaintChrome(g, contentWidth, contentHeight);
        _rowTooltip.Paint(g, Font, SettingsMenuTooltipColor, ToWindow(new Rectangle(0, 0, contentWidth, contentHeight)),
            Style.HeaderBorderMode ? ThemedTitle : null);
    }
}
