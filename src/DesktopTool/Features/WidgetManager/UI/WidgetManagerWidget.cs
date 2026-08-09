using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Layouts.UI;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.WidgetManager.UI;

/// <summary>
/// "Widget Manager" widget - a third, independent proof that LayeredWidgetForm's own chrome
/// (move/resize/snap/rename/settings/theme/list) works for something that isn't a Fence or the
/// Layout Launcher. Lists the app's toggleable widgets/switches - Fences, Layout Launcher, Snap
/// Lines, Widget Snapping, Fence Trash Can - each as a fixed row (never added to/removed from,
/// unlike Layout Launcher's own saved-profile list) with an on/off switch and, for the three that
/// have somewhere to go, a row-specific action button, so all of them can be reached without
/// opening the tray menu. Everything not genuinely specific to this widget (theme derivation, the
/// Settings dropdown's default rows, button/border/title/list painting) is LayeredWidgetForm's
/// own - see its own class comment.
/// </summary>
internal sealed class WidgetManagerWidget : LayeredWidgetForm
{
    private const int OuterMarginPx = 13;
    private const int HeaderHeight = 28;
    // Same reasoning/value as FenceForm's own SettingsButtonOverhang - just enough extra band above
    // OuterMargin to fully contain a SettingsButtonHeight-tall button plus its SettingsButtonGap
    // breathing room (both base defaults, 22/6), with a little more room to spare.
    private const int ButtonBandOverhang = 19;
    private const int TopMarginWithButtons = OuterMarginPx + ButtonBandOverhang;

    private const int RowCountFixed = 5;
    private const int ListVerticalPadding = 8;
    private const int ListHorizontalPadding = 10;

    // Used to seed CreateParams/GetCurrentBody before the widget has ever been resized (see
    // WidgetManagerModel.Height's own "null until moved/resized once" comment) - unlike Layout
    // Launcher's own DefaultBodyHeight (a guess, since its row count varies), this widget's row
    // count is fixed, so the default is the exact height that fits all five rows plus padding, not
    // an arbitrary constant.
    private static int DefaultBodyHeight => HeaderHeight + ListVerticalPadding * 2 + RowCountFixed * ListRowHeightConst;
    private const int ListRowHeightConst = 30;

    private readonly LayoutLauncherWidget _layoutLauncher;
    private readonly WidgetManagerModel _model;
    private readonly WidgetManagerStore _store;

    private bool _allowClose;
    private bool _settingsButtonArmed;

    // Cog for an "open an editor" action (Snap Lines/Layout Launcher), Plus for an "add one more"
    // action (Fences), None for a row with no editor of its own (Widget Snapping - a plain on/off
    // with nowhere to go) - see PaintCogGlyph/PaintPlusGlyph/PaintRowButton.
    private enum RowButtonIcon { Plus, Cog, None }

    private readonly record struct WidgetRow(string Label, string ButtonTooltip, RowButtonIcon ButtonIcon);

    // Fence Trash Can last - IsRowOn/ToggleRow/FireRowButtonAction's own index switches below must
    // stay in this same order.
    private static readonly WidgetRow[] Rows =
    {
        new("Fences", "Add Fence", RowButtonIcon.Plus),
        new("Layout Launcher", "Edit Layouts", RowButtonIcon.Cog),
        new("Snap Lines", "Edit Snap Lines", RowButtonIcon.Cog),
        new("Widget Snapping", string.Empty, RowButtonIcon.None),
        new("Fence Trash Can", string.Empty, RowButtonIcon.None),
    };

    // Row click handling - clicking a row's own switch flips that widget's on/off state; its
    // action button runs the row-specific command instead. Same arm-then-fire pattern as every
    // other button on this base (armed on OnMouseDown, fired on the matching OnMouseUp only if the
    // cursor is still over the same target), just local to this widget rather than a
    // LayeredWidgetForm mechanism - see LayoutLauncherWidget's own RowAction for the precedent.
    private enum RowTarget { None, Switch, Button }
    private RowTarget _armedRowTarget = RowTarget.None;
    private int _armedRowIndex = -1;

    // Row hover feedback - "Turn Fences Off"/"Add Fence"/"Edit Snap Lines"/etc, the only feedback
    // that a row's switch/button is clickable at all otherwise, since (unlike Settings/ChromeButton/
    // ContentButton) list rows don't get the base's own hover tint - doubly so now that the action
    // button is an icon rather than a self-explanatory text label. Same PaintedTooltip
    // (DesktopTool.UI) as LayoutLauncherWidget's own row tooltip, for the same "System.Windows.
    // Forms.ToolTip flashes its default look for a frame" reason - see that class's own comment.
    private readonly PaintedTooltip _rowTooltip = new();

    /// <summary>"?" (opens the Readme window) chained right off Settings/Copy Settings, then Close
    /// (× - hides, same as Layout Launcher's own "x") at the outer edge - LayeredWidgetForm's own
    /// ChromeButton mechanism instead of hand-rolled rect-chaining/paint/hit-test/arm-fire code.</summary>
    protected override IReadOnlyList<ChromeButton> ExtraButtons { get; }

    /// <summary>Fired when the Layout Launcher row's "Edit Layouts" button is clicked - lets
    /// TrayApplicationContext open the same Manage Layouts editor its own tray item does, without
    /// this widget needing a LayoutManager/LayoutEditorForm reference of its own.</summary>
    public event EventHandler? EditLayoutsRequested;

    /// <summary>Fired when the "?" button is clicked - lets TrayApplicationContext open (or
    /// re-activate) the Readme window the same "create once, reuse" way as OpenLayoutEditor, without
    /// this widget needing a ReadmeForm reference of its own.</summary>
    public event EventHandler? HelpRequested;

    protected override int OuterMargin => OuterMarginPx;
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMarginWithButtons;
    protected override int BottomBand => ButtonRowAtBottom ? TopMarginWithButtons : OuterMargin;
    protected override int MaxTopBand => TopMarginWithButtons;

    /// <summary>Which model LayeredWidgetForm's own theme derivation and default Settings-dropdown
    /// rows read from - WidgetManagerModel already implements IWidgetStyle.</summary>
    protected override IWidgetStyle Style => _model;

    public WidgetManagerWidget(FenceManager fenceManager, LayoutLauncherWidget layoutLauncher, WidgetManagerModel model, WidgetManagerStore store)
        : base(model.Opacity / 100f, fenceManager)
    {
        _layoutLauncher = layoutLauncher;
        _model = model;
        _store = store;

        ExtraButtons = new List<ChromeButton>
        {
            new("?", 22, () => HelpRequested?.Invoke(this, EventArgs.Empty)),
            new("×", 22, HideAndPersist),
        };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // LayeredWidgetForm's own default rename hit-testing/EditBox/title-context-menu/PaintChrome
        // all measure and draw against Control.Font, so this needs setting explicitly (WinForms'
        // own default is Microsoft Sans Serif).
        Font = AppTheme.Font;

        // Forces handle creation now that every field CreateParams needs is set.
        RenderAndPresent();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;

            // Control's base constructor probes CreateParams before our own constructor body has
            // run (so _model is still null at that point) - the real, model-driven CreateParams
            // request comes later, when the constructor body first touches Handle.
            if (_model is null)
                return cp;

            var bodyX = _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2;
            var bodyY = _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - DefaultBodyHeight) / 2;
            var bodyHeight = _model.Height ?? DefaultBodyHeight;

            ButtonRowAtBottom = ComputeButtonRowAtBottom(new Point(bodyX, bodyY), TopMarginWithButtons);

            cp.Width = _model.Width + OuterMargin * 2;
            cp.Height = bodyHeight + TopBand + BottomBand;
            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_CLIPCHILDREN;
            // Not WS_EX_TOPMOST - same ordinary (non-always-on-top) window style as FenceForm's own
            // CreateParams, so this doesn't sit above every other app's window on screen forever; it
            // just behaves like any other normal top-level window (still WS_EX_TOOLWINDOW, so it has
            // no taskbar button/Alt-Tab entry of its own, matching a Fence).
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
            cp.X = bodyX - OuterMargin;
            cp.Y = bodyY - TopBand;
            return cp;
        }
    }

    /// <summary>Repaints the row list against Fences/Layout Launcher's own live current state -
    /// needed once, right after TrayApplicationContext finishes constructing every widget and
    /// calling FenceManager.LoadAndShowAll/LayoutLauncherWidget.Show. This widget's own constructor
    /// already forces one RenderAndPresent to create its handle, but that runs before those two
    /// calls - at that point no fence has been shown yet and the Layout Launcher widget hasn't been
    /// shown either, so Fences.AnyVisible/layoutLauncher.Visible both read false and get baked into
    /// the very first pushed bitmap. Nothing else repaints this widget afterward on its own (a
    /// layered window's visible pixels are exactly whatever was last pushed via
    /// UpdateLayeredWindow, not re-derived from a WM_PAINT the OS might send), so without this call
    /// the Fences/Layout Launcher rows would keep showing stale "Off" until some unrelated repaint
    /// (hovering a row, say) happened to catch them up.</summary>
    public void RefreshRowStates() => RenderAndPresent();

    /// <summary>Shows (persisting Visible) if currently hidden, hides (persisting Visible) if
    /// currently shown - what the tray's "Widget Manager" checkbox toggles.</summary>
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
    }

    private void HideAndPersist()
    {
        Hide();
        _model.Visible = false;
        Persist();
    }

    /// <summary>Real disposal, for actual app shutdown (TrayApplicationContext.OnExit) - the only
    /// caller allowed to bypass OnFormClosing's cancel-and-hide below.</summary>
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
        // Nothing owned - the row tooltip is hand-painted, not a native control needing disposal.
    }

    protected override Rectangle GetCurrentBody() => new(
        _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2,
        _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - DefaultBodyHeight) / 2,
        _model.Width,
        _model.Height ?? DefaultBodyHeight);

    protected override int SnapMargin => _model.Margin;

    // ComputeMovedBody/ComputeResizedBody/BeginSnapDrag/SupportsResize/ResizableEdges all use
    // LayeredWidgetForm's own defaults unchanged - full, unrestricted resize on every edge, snapping
    // against every other live widget's edges (fences, Layout Launcher, this widget itself excluded)
    // and custom snap lines the same as any other widget on this base.

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

    // OnResized needs no override of its own - LayeredWidgetForm's own default (repositioning an
    // already-open Settings dropdown after a resize) already covers it.

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
            || GetCopySettingsButtonRect(contentWidth, onLeft).Contains(contentPoint)
            || TryGetExtraButtonAt(contentWidth, onLeft, contentPoint, out _)))
            return HTCLIENT;

        // Every row's switch/button lives inside the list area, already ordinary HTCLIENT
        // territory below - no extra carve-out needed here, unlike the margin-band Settings/extra
        // buttons above.

        if (ShowsButtons)
        {
            // Same "margin becomes a move handle only once activated" pattern as FenceForm.HitTest -
            // see its own comment for why.
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

        // HTBORDER, not HTCAPTION - a left-button drag from the title row itself doesn't move the
        // widget (only the margin does, once active - see above); right-click/double-click/hover
        // still work (see HTBORDER's own comment on LayeredWidgetForm).
        if (!_model.HideTitle && y - TopBand <= HeaderHeight)
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

        if (ShowsButtons && GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint))
        {
            _settingsButtonArmed = true;
            return;
        }
        if (ShowsButtons && TryArmCopySettingsButton(contentPoint))
            return;
        if (ShowsButtons && TryArmExtraButton(contentPoint))
            return;
        if (TryHandleListMouseDown(contentPoint))
            return;

        var (target, index) = GetRowTargetAt(contentPoint);
        if (target != RowTarget.None)
        {
            _armedRowTarget = target;
            _armedRowIndex = index;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateListScrollDrag(ToContent(e.Location));
        UpdateRowTooltip(e.Location);
    }

    // OnMouseEnter needs no override of its own - LayeredWidgetForm's own already covers hover
    // tracking. OnMouseLeave still needs one, to hide a row tooltip left showing right as the cursor
    // leaves - same reasoning/shape as LayoutLauncherWidget's own OnMouseLeave override.
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

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            if (ShowsButtons && GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint))
                OpenSettingsMenu();
            return;
        }

        FireArmedCopySettingsButton(contentPoint);
        FireArmedExtraButton(contentPoint);
        EndListScrollDrag();

        if (_armedRowTarget != RowTarget.None)
        {
            var armedTarget = _armedRowTarget;
            var armedIndex = _armedRowIndex;
            _armedRowTarget = RowTarget.None;
            _armedRowIndex = -1;

            var (target, index) = GetRowTargetAt(contentPoint);
            if (target == armedTarget && index == armedIndex)
                FireRowTarget(target, index);
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

    protected override bool HideTitle
    {
        get => _model.HideTitle;
        set
        {
            _model.HideTitle = value;
            Persist();
            RenderAndPresent();
        }
    }

    // TargetOpacity/EditBoxTextColor/EditBoxBackgroundColor/ChromeMenuFieldColor/ChromeMenuHoverColor/
    // SettingsMenu* are all LayeredWidgetForm's own default now. Every IWidgetStyle property (color,
    // Header Darkness, Opacity, Full Opacity When Active, Tint Strength, Margin, Corner Radius, Font
    // Size, Align, Header Border Mode) is mutated directly against Style (== _model) by the base
    // itself now - this widget doesn't need a dedicated SetHeaderDarkness/SetOpacity/etc. override of
    // its own for any of them, just this one persistence hook.
    protected override void PersistStyle() => Persist();

    private const int CmdToggleStartWithWindows = 1;
    private const int CmdToggleShowHiddenFiles = 2;
    private const int CmdToggleAlwaysMaxRows = 3;

    /// <summary>Rows Shown/Always Max Rows first - same stepper-plus-checkbox pair as
    /// LayoutLauncherWidget's own additional rows (see its own doc comment for why a stepper rather
    /// than a checkbox), just capped at RowCountFixed instead of a variable saved-profile count -
    /// then Start with Windows/Show Hidden Files, the same two system-level toggles the tray menu
    /// itself still shows, mirrored here now that Widget Manager is the entry point for most of what
    /// used to live only in the tray (see TrayApplicationContext). Checked reads
    /// StartupManager.IsEnabled/HiddenFilesManager.IsEnabled fresh on every open, same as the tray's
    /// own items - both are system state Desktop Tool doesn't own, so a change made outside this menu
    /// (or the tray's own copy of these two) is never left showing stale here either.</summary>
    protected override IReadOnlyList<DropdownMenu.Row>? BuildAdditionalSettingsRows() => new List<DropdownMenu.Row>
    {
        new(0, "Rows Shown", IsHeader: true),
        new(0, string.Empty, IsStepper: true,
            StepperValue: () => _model.RowsShown,
            OnStepperChange: rows => SetRowsShown(Math.Clamp(rows, 1, RowCountFixed)),
            StepperMin: 1, StepperMax: RowCountFixed, StepperStep: 1, StepperSuffix: "",
            IsEnabled: () => !_model.AlwaysMaxRows),
        new(CmdToggleAlwaysMaxRows, "Always Max Rows", HasCheckbox: true,
            IsChecked: () => _model.AlwaysMaxRows,
            Tooltip: "Rows Shown always shows every row - the list grows to fit them without "
                + "resizing the widget itself"),
        new(CmdToggleStartWithWindows, "Start with Windows", HasCheckbox: true, IsChecked: () => StartupManager.IsEnabled),
        new(CmdToggleShowHiddenFiles, "Show Hidden Files", HasCheckbox: true, IsChecked: () => HiddenFilesManager.IsEnabled),
    };

    protected override void HandleSettingsCommand(int id)
    {
        switch (id)
        {
            case CmdToggleAlwaysMaxRows:
                _model.AlwaysMaxRows = !_model.AlwaysMaxRows;
                Persist();
                SyncRowsShownToMax();
                break;
            case CmdToggleStartWithWindows:
                StartupManager.SetEnabled(!StartupManager.IsEnabled);
                break;
            case CmdToggleShowHiddenFiles:
                HiddenFilesManager.SetEnabled(!HiddenFilesManager.IsEnabled);
                break;
            default:
                base.HandleSettingsCommand(id);
                break;
        }
    }

    /// <summary>Applies a Rows Shown change - same "resize the widget itself by exactly a row's
    /// worth of height" behavior as LayoutLauncherWidget.SetRowsShown (see its own doc comment for
    /// why an absolute target height, not a delta).</summary>
    private void SetRowsShown(int rows)
    {
        if (rows == _model.RowsShown)
            return;
        _model.RowsShown = rows;
        Persist();
        SetBodyHeight(HeightForRows(rows));
    }

    /// <summary>Keeps Rows Shown pinned to RowCountFixed while AlwaysMaxRows is on - a no-op
    /// otherwise. Unlike SetRowsShown, this never resizes the widget's own body (see
    /// LayoutLauncherWidget.SyncRowsShownToMax's own doc comment for the same distinction there) -
    /// only meaningful right when the toggle is turned on, since this widget's own row count never
    /// changes on its own the way a saved-layout count can.</summary>
    private void SyncRowsShownToMax()
    {
        if (!_model.AlwaysMaxRows)
            return;
        _model.RowsShown = RowCountFixed;
        Persist();
        RenderAndPresent();
    }

    /// <summary>The body height that fits exactly n rows, at this widget's own fixed header/padding
    /// overhead - same formula GetListArea itself measures against, just solved for total height.</summary>
    private int HeightForRows(int rows) =>
        (_model.HideTitle ? 0 : HeaderHeight) + ListVerticalPadding * 2 + rows * ListRowHeightConst;

    /// <summary>Sets the widget's own persisted+actual body height directly, keeping its top edge
    /// fixed (only the bottom edge moves) - same SetWindowPos approach as LayoutLauncherWidget's own
    /// SetBodyHeight.</summary>
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

    /// <summary>Everything between the header and the list's own bottom padding - the list itself
    /// never grows taller than min(RowsShown, RowCountFixed) rows (see WidgetManagerModel.RowsShown),
    /// so a taller body than that just leaves blank space below the list rather than stretching it,
    /// same as LayoutLauncherWidget.GetListArea's own doc comment describes for its own list.</summary>
    protected override Rectangle GetListArea(int contentWidth, int contentHeight)
    {
        var top = (_model.HideTitle ? 0 : HeaderHeight) + ListVerticalPadding;
        var available = Math.Max(ListRowHeight, contentHeight - top - ListVerticalPadding);
        var wanted = Math.Min(_model.RowsShown, RowCountFixed) * ListRowHeight;
        var height = Math.Max(ListRowHeight, Math.Min(available, wanted));
        return new Rectangle(ListHorizontalPadding, top, contentWidth - ListHorizontalPadding * 2, height);
    }

    protected override int ListRowCount => RowCountFixed;
    protected override int ListRowHeight => ListRowHeightConst;

    private const int SwitchWidth = 38;
    private const int SwitchHeight = 18;
    private const int SwitchRightPadding = 4;
    private const int ButtonRightGap = 8;
    private const int ButtonSize = 20;

    /// <summary>Every row's own switch rect, flush against the row's right edge - pure relative math
    /// off rowRect's own edges, so it works whether rowRect is window-space (called from
    /// PaintListRow) or content-space (called from GetRowTargetAt below), as long as the caller is
    /// consistent about which - same convention LayoutLauncherWidget's own GetRowButtonRects uses.</summary>
    private static Rectangle GetRowSwitchRect(Rectangle rowRect)
    {
        var y = rowRect.Y + (rowRect.Height - SwitchHeight) / 2;
        return new Rectangle(rowRect.Right - SwitchRightPadding - SwitchWidth, y, SwitchWidth, SwitchHeight);
    }

    /// <summary>The row's own action button rect, sitting just left of the switch - a fixed square
    /// (unlike the old text-label button, an icon glyph doesn't need per-row measured width).</summary>
    private static Rectangle GetRowButtonRect(Rectangle rowRect)
    {
        var switchRect = GetRowSwitchRect(rowRect);
        var y = rowRect.Y + (rowRect.Height - ButtonSize) / 2;
        return new Rectangle(switchRect.X - ButtonRightGap - ButtonSize, y, ButtonSize, ButtonSize);
    }

    /// <summary>The row's own label area - from the row's own left padding up to where its action
    /// button starts, or (hasButton false, e.g. Widget Snapping) straight up to the switch itself,
    /// since there's no button rect to stop at.</summary>
    private static Rectangle GetRowLabelRect(Rectangle rowRect, bool hasButton)
    {
        var stopX = hasButton ? GetRowButtonRect(rowRect).X : GetRowSwitchRect(rowRect).X;
        var width = Math.Max(0, stopX - 8 - rowRect.X - 8);
        return new Rectangle(rowRect.X + 8, rowRect.Y, width, rowRect.Height);
    }

    /// <summary>Which row (if any) contentPoint lands on, and that row's own current content-relative
    /// rect - mirrors the exact row-position math PaintList itself uses, just for a click instead of
    /// a paint. Local to this widget, not a base mechanism - see LayoutLauncherWidget's own
    /// TryGetRowAt for the same pattern.</summary>
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

    /// <summary>What clicking contentPoint would do right now - RowTarget.None if it doesn't land on
    /// a row's switch or action button at all (unlike Layout Launcher's rows, clicking a bare label
    /// here does nothing - there's no "run" action for a row that isn't a saved layout). A row whose
    /// own ButtonIcon is None (Widget Snapping) has no button rect to test at all - its label area
    /// already reaches the switch (see GetRowLabelRect), so there's no gap to accidentally hit.</summary>
    private (RowTarget Target, int Index) GetRowTargetAt(Point contentPoint)
    {
        if (!TryGetRowAt(contentPoint, out var index, out var rowRect))
            return (RowTarget.None, -1);

        if (GetRowSwitchRect(rowRect).Contains(contentPoint))
            return (RowTarget.Switch, index);
        if (Rows[index].ButtonIcon != RowButtonIcon.None && GetRowButtonRect(rowRect).Contains(contentPoint))
            return (RowTarget.Button, index);
        return (RowTarget.None, -1);
    }

    /// <summary>Whether row index's widget is currently "on" - Fences: at least one fence visible
    /// (see FenceManager.AnyVisible); Layout Launcher: the widget's own Visible; Snap Lines:
    /// SnapLineManager.Enabled; Widget Snapping: SnapLineManager.WidgetEdgesEnabled; Fence Trash
    /// Can: FenceManager.HasRecycleBin. Same row order as Rows above.</summary>
    private bool IsRowOn(int index) => index switch
    {
        0 => Fences.AnyVisible,
        1 => _layoutLauncher.Visible,
        2 => Fences.SnapLines.Enabled,
        3 => Fences.SnapLines.WidgetEdgesEnabled,
        4 => Fences.HasRecycleBin,
        _ => false,
    };

    private void ToggleRow(int index)
    {
        switch (index)
        {
            case 0: Fences.SetAllVisible(!Fences.AnyVisible); break;
            case 1: _layoutLauncher.ToggleVisible(); break;
            case 2: Fences.SnapLines.SetEnabled(!Fences.SnapLines.Enabled); break;
            case 3: Fences.SnapLines.SetWidgetEdgesEnabled(!Fences.SnapLines.WidgetEdgesEnabled); break;
            case 4:
                if (Fences.HasRecycleBin) Fences.RemoveRecycleBin(); else Fences.AddRecycleBin();
                break;
        }
    }

    private void FireRowButtonAction(int index)
    {
        switch (index)
        {
            case 0: Fences.CreateFence(); break;
            case 1: EditLayoutsRequested?.Invoke(this, EventArgs.Empty); break;
            case 2: Fences.SnapLines.EnterEditMode(); break;
            // 3 (Widget Snapping) and 4 (Fence Trash Can) have no action button - RowButtonIcon.
            // None, never hit-testable.
        }
    }

    /// <summary>Switch flips that row's own on/off state and repaints (the switch's own fill/label
    /// depend on it); the action button runs its row-specific command - neither needs a repaint of
    /// its own beyond what Fences/SnapLines/LayoutLauncher's own state change already triggers via
    /// their normal paths (a fence showing/hiding, the edit overlay opening, Layout Launcher itself
    /// showing/hiding), except the switch flip, whose new on/off color/label only lives on this
    /// widget's own canvas.</summary>
    private void FireRowTarget(RowTarget target, int index)
    {
        if (index < 0 || index >= Rows.Length)
            return;

        switch (target)
        {
            case RowTarget.Switch:
                ToggleRow(index);
                RenderAndPresent();
                break;
            case RowTarget.Button:
                FireRowButtonAction(index);
                break;
        }
    }

    private string SwitchTooltipText(int index) => IsRowOn(index) ? $"Turn {Rows[index].Label} Off" : $"Turn {Rows[index].Label} On";

    /// <summary>Shows/hides "Turn {row} On/Off" over a row's switch, or "Add Fence"/"Edit Snap
    /// Lines"/"Edit Layouts" over its action button, via the shared PaintedTooltip - the only
    /// affordance that either is clickable at all, now that the button is an icon rather than a
    /// self-explanatory text label.</summary>
    private void UpdateRowTooltip(Point windowLocation)
    {
        var contentPoint = ToContent(windowLocation);
        var (target, index) = GetRowTargetAt(contentPoint);

        var changed = target switch
        {
            RowTarget.Switch => _rowTooltip.Show(SwitchTooltipText(index), ToWindow(GetRowSwitchRect(GetRowRectFor(index)))),
            RowTarget.Button => _rowTooltip.Show(Rows[index].ButtonTooltip, ToWindow(GetRowButtonRect(GetRowRectFor(index)))),
            _ => _rowTooltip.Hide(),
        };

        if (changed)
            RenderAndPresent();
    }

    /// <summary>Recomputes a row's own current content-relative rect from its index alone - only
    /// needed by UpdateRowTooltip, since GetRowTargetAt already found the row but only returns its
    /// index, not the rect TryGetRowAt computed along the way.</summary>
    private Rectangle GetRowRectFor(int index)
    {
        var size = GetContentSize();
        var area = GetListArea(size.Width, size.Height);
        var maxScroll = Math.Max(0, ListRowCount * ListRowHeight - area.Height);
        var rowWidth = maxScroll > 0 ? area.Width - (Scrollbar.Width + Scrollbar.Margin * 2) : area.Width;
        var rowTop = area.Top + index * ListRowHeight - ListScrollOffset;
        return new Rectangle(area.Left, rowTop, rowWidth, ListRowHeight);
    }

    /// <summary>Label, action button, and an on/off switch pill at the row's own right edge -
    /// alternates ThemedListRow/ThemedListRowDark by index so rows read as banded rather than one
    /// flat surface, same as LayoutLauncherWidget's own rows.</summary>
    protected override void PaintListRow(Graphics g, int index, Rectangle rowRect)
    {
        var rowBackground = index % 2 == 0 ? ThemedListRow : ThemedListRowDark;
        using (var rowFill = new SolidBrush(rowBackground))
            g.FillRectangle(rowFill, rowRect);

        var icon = Rows[index].ButtonIcon;
        var hasButton = icon != RowButtonIcon.None;

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(Color.WhiteSmoke))
        using (var textFormat = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(Rows[index].Label, Font, textBrush, GetRowLabelRect(rowRect, hasButton), textFormat);
        g.TextRenderingHint = previousTextHint;

        if (hasButton)
            PaintRowButton(g, GetRowButtonRect(rowRect), icon);
        PaintRowSwitch(g, GetRowSwitchRect(rowRect), IsRowOn(index), rowBackground);
    }

    /// <summary>Dispatches to the row's own icon glyph - transparent, no button-shaped fill of its
    /// own (unlike Settings/ChromeButton/ContentButton), the same "the glyph sits directly on the
    /// row's own background" treatment LayoutLauncherWidget's own PaintCopyGlyph/PaintDeleteGlyph
    /// use for their row buttons.</summary>
    private static void PaintRowButton(Graphics g, Rectangle rect, RowButtonIcon icon)
    {
        switch (icon)
        {
            case RowButtonIcon.Plus:
                PaintPlusGlyph(g, rect);
                break;
            case RowButtonIcon.Cog:
                PaintCogGlyph(g, rect);
                break;
        }
    }

    /// <summary>"Add Fence" - a plain cross, same hand-drawn construction as DropdownMenu's own Plus
    /// grid glyph (Custom... color pick) but without the circle around it, since this button has no
    /// background of its own to sit inside.</summary>
    private static void PaintPlusGlyph(Graphics g, Rectangle rect)
    {
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        const float half = 5f;
        using var pen = new Pen(Color.WhiteSmoke, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - half, cy, cx + half, cy);
        g.DrawLine(pen, cx, cy - half, cx, cy + half);
    }

    /// <summary>"Edit Snap Lines"/"Edit Layouts" - a simplified gear: an outer ring, a smaller inner
    /// ring for the center hole, and eight teeth rotated evenly around it. No icon asset library in
    /// this app (see WarningIcon's own comment), so this is drawn the same "just the shape" way as
    /// every other glyph here rather than an embedded image.</summary>
    private static void PaintCogGlyph(Graphics g, Rectangle rect)
    {
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        var outerRadius = Math.Min(rect.Width, rect.Height) * 0.16f;
        var innerRadius = outerRadius * 0.45f;
        var toothLength = outerRadius * 0.5f;
        var toothWidth = outerRadius * 0.55f;
        const int toothCount = 8;

        var previousSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var pen = new Pen(Color.WhiteSmoke, 1.3f))
        {
            g.DrawEllipse(pen, cx - outerRadius, cy - outerRadius, outerRadius * 2, outerRadius * 2);
            g.DrawEllipse(pen, cx - innerRadius, cy - innerRadius, innerRadius * 2, innerRadius * 2);
        }

        using var toothBrush = new SolidBrush(Color.WhiteSmoke);
        for (var i = 0; i < toothCount; i++)
        {
            var angle = i * (Math.PI * 2 / toothCount);
            var midRadius = outerRadius + toothLength / 2f;
            var toothCenterX = cx + (float)(Math.Cos(angle) * midRadius);
            var toothCenterY = cy + (float)(Math.Sin(angle) * midRadius);

            var state = g.Save();
            g.TranslateTransform(toothCenterX, toothCenterY);
            g.RotateTransform((float)(angle * 180 / Math.PI));
            g.FillRectangle(toothBrush, -toothLength / 2f, -toothWidth / 2f, toothLength, toothWidth);
            g.Restore(state);
        }

        g.SmoothingMode = previousSmoothing;
    }

    /// <summary>A small hand-drawn on/off pill - no existing toggle-switch control in this app to
    /// reuse (every other checkbox-shaped control here, DropdownMenu's own HasCheckbox rows
    /// included, is a plain checkbox, not a switch), so this is a new glyph in the same "no icon
    /// asset library, just draw the shape" style as WarningIcon/LayoutLauncherWidget's own
    /// PaintCopyGlyph/PaintDeleteGlyph. On is a solid white pill with its "On" text punched out in
    /// the row's own background color (rowBackground - the same alternating ThemedListRow/
    /// ThemedListRowDark fill the row itself sits on), so the label reads as a cutout rather than
    /// printed text sitting on top; off stays outlined only, in ThemedCheckboxBorder, with plain
    /// WhiteSmoke text - unlike on, off has no fill of its own for the label to contrast against.</summary>
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

    // A smaller font than the widget's own Control.Font (AppTheme.Font, 9pt) - "On"/"Off" at 9pt
    // doesn't fit inside an 18px-tall pill without either clipping or forcing the pill itself
    // taller than a row comfortably fits. Created once, not per-paint - a fresh Font on every single
    // repaint would be wasteful, same reasoning as LayeredWidgetForm's own cached _titleFont.
    private readonly Font _switchFont = new(AppTheme.Font.FontFamily, 7f);
    private Font SwitchFont => _switchFont;

    /// <summary>Body/title/border/Settings/Close/the list itself are all LayeredWidgetForm's own
    /// PaintChrome now (see ExtraButtons/GetListArea/PaintListRow) - the row tooltip (see
    /// _rowTooltip/UpdateRowTooltip) is the only thing genuinely specific to this widget still
    /// painted here, last, so it sits on top of everything else.</summary>
    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        PaintChrome(g, contentWidth, contentHeight);
        _rowTooltip.Paint(g, Font, SettingsMenuTooltipColor, ToWindow(new Rectangle(0, 0, contentWidth, contentHeight)),
            Style.HeaderBorderMode ? ThemedTitle : null);
    }
}
