using System.Drawing.Text;
using DesktopTool.Features.Fences;
using DesktopTool.Features.FolderFences;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.JunkDrawer.UI;

/// <summary>
/// "Junk Drawer" widget - a home for small system-level tools that don't belong to any one feature:
/// Show All Fences (the tray's Show/Hide All), Show Hidden Files and Start with Windows, each a row
/// with an on/off switch. Clicking anywhere on a row flips it. The tray menu keeps its own copy of
/// each; every copy repaints when another changes the setting (see the Changed/VisibilityChanged
/// subscriptions below). Same chrome, row-click and tooltip plumbing as WidgetManagerWidget.
/// </summary>
internal sealed class JunkDrawerWidget : LayeredWidgetForm
{
    private const int OuterMarginPx = 13;
    private const int HeaderHeight = 28;
    private const int ButtonBandOverhang = 19;
    private const int TopMarginWithButtons = OuterMarginPx + ButtonBandOverhang;

    private const int ListVerticalPadding = 8;
    private const int ListHorizontalPadding = 10;
    private const int ListRowHeightConst = 30;
    private const int SwitchRightPadding = 4;

    /// <summary>One tool. Adding another is one more entry in the constructor's Tools list.</summary>
    private readonly record struct Tool(string Label, Func<bool> IsOn, Action Toggle);

    private readonly IReadOnlyList<Tool> _tools;
    private readonly FolderFenceManager _folderFences;
    private readonly JunkDrawerModel _model;
    private readonly JunkDrawerStore _store;
    private readonly PaintedTooltip _rowTooltip = new();

    private bool _allowClose;
    private bool _settingsButtonArmed;
    private int _armedRowIndex = -1;

    protected override IReadOnlyList<ChromeButton> ExtraButtons { get; }

    /// <summary>Fired whenever Visible actually changes, so Widget Manager's row switch stays current.</summary>
    public event EventHandler? VisibilityChanged;

    protected override int OuterMargin => OuterMarginPx;
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMarginWithButtons;
    protected override int BottomBand => ButtonRowAtBottom ? TopMarginWithButtons : OuterMargin;
    protected override int MaxTopBand => TopMarginWithButtons;
    protected override IWidgetStyle Style => _model;
    protected override string HelpEntry => "Junk Drawer";

    private int DefaultBodyHeight => HeaderHeight + ListVerticalPadding * 2 + _tools.Count * ListRowHeightConst;

    public JunkDrawerWidget(FenceManager fenceManager, FolderFenceManager folderFences, JunkDrawerModel model, JunkDrawerStore store)
        : base(model.Opacity / 100f, fenceManager)
    {
        _folderFences = folderFences;
        // Before _model: CreateParams' default height reads the tool count, and only checks _model.
        _tools = new List<Tool>
        {
            new("Show All Fences", () => Fences.AnyVisible || _folderFences.AnyVisible, ToggleAllFences),
            new("Show Hidden Files", () => HiddenFilesManager.IsEnabled, () => HiddenFilesManager.SetEnabled(!HiddenFilesManager.IsEnabled)),
            new("Start with Windows", () => StartupManager.IsEnabled, () => StartupManager.SetEnabled(!StartupManager.IsEnabled)),
        };
        _model = model;
        _store = store;

        // Repaint whenever another copy of a switch (tray menu, Widget Manager's Fences row) changes it.
        Fences.VisibilityChanged += OnToolStateChanged;
        _folderFences.VisibilityChanged += OnToolStateChanged;
        HiddenFilesManager.Changed += OnToolStateChanged;
        StartupManager.Changed += OnToolStateChanged;

        ExtraButtons = new List<ChromeButton>
        {
            new("×", 22, HideAndPersist, "Hide Junk Drawer"),
        };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = AppTheme.Font;

        RenderAndPresent();
    }

    /// <summary>Same as the tray's Show/Hide All: shows everything if nothing is showing, otherwise
    /// hides everything - fences and folder fences together.</summary>
    private void ToggleAllFences()
    {
        var visible = !(Fences.AnyVisible || _folderFences.AnyVisible);
        Fences.SetAllVisible(visible);
        _folderFences.SetAllVisible(visible);
    }

    private void OnToolStateChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed)
            RenderAndPresent();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_model is null)
                return cp;

            var body = GetCurrentBody();
            ButtonRowAtBottom = ComputeButtonRowAtBottom(body.Location, TopMarginWithButtons);

            cp.Width = body.Width + OuterMargin * 2;
            cp.Height = body.Height + TopBand + BottomBand;
            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_CLIPCHILDREN;
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
            cp.X = body.X - OuterMargin;
            cp.Y = body.Y - TopBand;
            return cp;
        }
    }

    /// <summary>Repaints against the tools' live state - for TrayApplicationContext to call once every
    /// fence has been shown at startup (see WidgetManagerWidget.RefreshRowStates for why).</summary>
    public void RefreshRowStates() => RenderAndPresent();

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

    protected override void DisposeOwnedResources()
    {
        Fences.VisibilityChanged -= OnToolStateChanged;
        _folderFences.VisibilityChanged -= OnToolStateChanged;
        HiddenFilesManager.Changed -= OnToolStateChanged;
        StartupManager.Changed -= OnToolStateChanged;
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

    // ---- Mouse ----

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
        if (TryHandleListMouseDown(contentPoint))
            return;

        if (TryGetRowAt(contentPoint, out var index, out _))
            _armedRowIndex = index;
    }

    /// <summary>A tool's setting can also change outside the app (Explorer's Folder Options, Task
    /// Manager's startup list) with no event to hear about it - so the switches are re-read the moment
    /// the mouse comes over the widget, and it's never showing stale state while you're using it.</summary>
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        RenderAndPresent();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateListScrollDrag(ToContent(e.Location));
        UpdateRowTooltip(e.Location);
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
        EndListScrollDrag();

        if (_armedRowIndex >= 0)
        {
            var armed = _armedRowIndex;
            _armedRowIndex = -1;
            if (TryGetRowAt(contentPoint, out var index, out _) && index == armed)
            {
                _tools[index].Toggle();
                // The tool's own event usually repaints already; this covers one that doesn't, and
                // refreshes the tooltip's "Turn ... On/Off" wording for the new state.
                UpdateRowTooltip(e.Location, force: true);
                RenderAndPresent();
            }
        }
    }

    // ---- Rows ----

    protected override Rectangle GetListArea(int contentWidth, int contentHeight)
    {
        var top = (_model.HideHeader ? 0 : HeaderHeight) + ListVerticalPadding;
        var height = Math.Max(ListRowHeight, contentHeight - top - ListVerticalPadding);
        return new Rectangle(ListHorizontalPadding, top, contentWidth - ListHorizontalPadding * 2, height);
    }

    protected override int ListRowCount => _tools.Count;
    protected override int ListRowHeight => ListRowHeightConst;

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

    private static Rectangle GetSwitchRect(Rectangle rowRect)
    {
        var y = rowRect.Y + (rowRect.Height - ToggleSwitch.Height) / 2;
        return new Rectangle(rowRect.Right - SwitchRightPadding - ToggleSwitch.Width, y, ToggleSwitch.Width, ToggleSwitch.Height);
    }

    private void UpdateRowTooltip(Point windowLocation, bool force = false)
    {
        var contentPoint = ToContent(windowLocation);
        bool changed;
        if (TryGetRowAt(contentPoint, out var index, out var rowRect))
        {
            var tool = _tools[index];
            var text = tool.IsOn() ? $"Turn {tool.Label} Off" : $"Turn {tool.Label} On";
            if (force)
                _rowTooltip.Hide();
            changed = _rowTooltip.Show(text, ToWindow(GetSwitchRect(rowRect)));
        }
        else
        {
            changed = _rowTooltip.Hide();
        }

        if (changed)
            RenderAndPresent();
    }

    protected override void PaintListRow(Graphics g, int index, Rectangle rowRect)
    {
        var rowBackground = index % 2 == 0 ? ThemedListRow : ThemedListRowDark;
        using (var rowFill = new SolidBrush(rowBackground))
            g.FillRectangle(rowFill, rowRect);

        var switchRect = GetSwitchRect(rowRect);
        var labelRect = new Rectangle(rowRect.X + 8, rowRect.Y, Math.Max(0, switchRect.X - 8 - rowRect.X - 8), rowRect.Height);

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(Color.WhiteSmoke))
        using (var textFormat = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(_tools[index].Label, Font, textBrush, labelRect, textFormat);
        g.TextRenderingHint = previousTextHint;

        ToggleSwitch.Paint(g, switchRect, _tools[index].IsOn(), rowBackground, ThemedCheckboxBorder);
    }

    // ---- Title/header plumbing (same as WidgetManagerWidget) ----

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

    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        PaintChrome(g, contentWidth, contentHeight);
        _rowTooltip.Paint(g, Font, SettingsMenuTooltipColor, ToWindow(new Rectangle(0, 0, contentWidth, contentHeight)),
            Style.HeaderBorderMode ? ThemedTitle : null);
    }
}
