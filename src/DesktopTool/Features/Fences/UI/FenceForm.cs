using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Fences.Native;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.Fences.UI;

/// <summary>
/// A real WinForms Form (custom-drawn, WS_POPUP, no native chrome) rather than a raw NativeWindow.
/// An earlier version used a raw NativeWindow to avoid Form/Control fighting SetParent-based
/// z-order embedding onto Progman/WorkerW - but that embedding strategy is currently disabled
/// (see FenceManager, which uses FloatingDesktopAnchorStrategy instead) so that concern doesn't
/// apply right now. Being a real Form matters for a different reason: drag-and-drop needs to
/// register as an OLE drop target, and a hand-rolled P/Invoke RegisterDragDrop/IDropTarget CCW
/// turned out not to reliably receive DragEnter/Drop callbacks, while WinForms' own
/// AllowDrop/OnDragEnter/OnDragDrop machinery does.
///
/// A fence owns its contents as a plain list of file paths (FenceModel.Files) and draws its own
/// icon+label for each one (PaintItems) - the same approach used by NoFences
/// (https://github.com/Twometer/NoFences), an open-source Stardock Fences alternative this app's
/// drag-and-drop model is based on (see README's Credits section). Dropping a file here just adds
/// a reference to it; if that file lives directly on the real desktop, its real icon gets moved
/// into a hidden folder so it isn't visible twice (see FenceManager.AddFiles and DesktopIconHider) -
/// anything dragged in from elsewhere is left completely alone.
///
/// Rendering is pushed via UpdateLayeredWindow (see LayeredWindowPresenter) rather than drawn in
/// response to WM_PAINT with a SetWindowRgn-clipped shape. The region approach was tried first and
/// works, but a GDI region is a hard-edged, non-antialiased mask, so the rounded corners always
/// came out as a visible pixel staircase no matter the radius. Per-pixel alpha draws a genuinely
/// smooth edge, and Windows uses that same alpha for hit-testing, so fully-transparent pixels
/// (outside the rounded corner) are naturally click-through with no region needed at all.
///
/// Move, resize, snap, rename, and the Settings button/dropdown are all LayeredWidgetForm's own now -
/// this class only supplies the small hooks those need (GetCurrentBody, Title, BuildSettingsRows,
/// etc.) plus everything genuinely fence-specific: the icon grid itself, OCD Fence Sizing, and the
/// z-order restack to the bottom (see OnDragEnd).
/// </summary>
internal sealed class FenceForm : LayeredWidgetForm
{
    internal const int TitleBarHeight = 26;
    // Extra invisible band around the visible fence, purely so the resize cursor is easier to
    // grab - only possible now that per-pixel alpha (not SetWindowRgn) defines the window's shape,
    // since Windows treats fully-transparent pixels as click-through; a hard region couldn't do
    // this at all (you can't hit-test past a window's own rectangle). Painted at a barely-non-zero
    // alpha (see MarginFillColor) since alpha 0 would be click-through too, defeating the point.
    private const int OuterMarginPx = 13;

    // The settings button sits above the fence, flush with its top-right corner, and doesn't fit
    // inside the plain OuterMargin band (13px) with any breathing room, so the window is extended
    // *only on top* by this much extra - every other edge (left/right/bottom, and their resize-grab
    // bands) stays exactly OuterMargin. Grown by the same +2 as SettingsButtonGap, so the breathing
    // room above the button row (between it and this window's own top edge) stays what it was
    // before that gap grew.
    private const int SettingsButtonOverhang = 19;
    private const int TopMargin = OuterMarginPx + SettingsButtonOverhang;
    // CornerRadius is a user-adjustable per-fence setting now (FenceModel.CornerRadius, via the
    // "Corner Radius" stepper - see BuildSettingsRows), not a fixed constant - LayeredWidgetForm's
    // own PaintChrome reads it directly off Style.

    // LayeredWidgetForm's own OuterMargin/TopBand/BottomBand/MaxTopBand contract, left entirely to
    // this override rather than generalized in the base - this fence's own split is asymmetric
    // (TopBand collapses to 0 once flipped rather than mirroring OuterMargin/TopMargin the way
    // BottomBand does, see its own comment for why), which is Fence-specific reasoning about
    // Fence's own margin band, not something worth generalizing from a single example.
    protected override int OuterMargin => OuterMarginPx;

    /// <summary>The margin band on whichever side currently holds the button row - see
    /// ButtonRowAtBottom. TopMargin-sized there, same as always; zero on the top side once flipped
    /// (see BottomBand below for why).</summary>
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMargin;

    /// <summary>The margin band on whichever side does NOT currently hold the button row - see
    /// ButtonRowAtBottom. Normally a plain OuterMargin, like the left/right/bottom edges always
    /// are - except once flipped, when TopBand above goes to 0 instead: whatever keeps this app's
    /// own drag loop from letting the fence's edge fully reach the screen's own edge (observed
    /// settling exactly OuterMargin short of it, every time, even after the flip first shrank it
    /// from TopMargin down to OuterMargin) reacts to any nonzero margin there at all, not just a
    /// wide one - only removing it outright lets the fence sit flush with the very top of the
    /// screen. The resize-grab hit-test zone on that side still isn't literally zero-width (see
    /// ResizeHitTest's own ResizeMargin addition), just without this extra invisible cushion beyond
    /// the body's own edge.</summary>
    protected override int BottomBand => ButtonRowAtBottom ? TopMargin : OuterMargin;

    protected override int MaxTopBand => TopMargin;

    // Every WM_*/HT* message/hit-test code with a shared home (move/resize/rename/paint/erase-bkgnd
    // codes) is LayeredWidgetForm's own now - nothing needs its own copy here anymore.

    // CmdToggleHideHeader/CmdToggleFullOpacityOnHover/CmdColorDefault/CmdColorCustom/CmdColorEyedrop/
    // CmdColorPresetBase are LayeredWidgetForm's own now (negative ids - see its own comment for why
    // that range can never collide with these).
    private const int CmdToggleHideLabels = 7;
    private const int CmdResizeBoth = 9;
    private const int CmdResizeLeftRight = 10;
    private const int CmdResizeTopDown = 11;
    private const int CmdToggleOcdSizing = 12;

    // Not a real WM_COMMAND id - just this row's own Row.Id value for the non-clickable "Fence
    // Dimensions" section header inside the OCD flyout (see BuildAdditionalSettingsRows;
    // DropdownMenu.Row.IsHeader rows don't dispatch a command either way, so this only needs to be
    // distinct from real command ids, never looked up). Header Darkness/Opacity/Tint Strength/Corner
    // Radius/Margin's own header tags are LayeredWidgetForm's own now (StyleMenuRows.Build).
    private const int TagFenceDimensionsHeader = 1004;

    private const int IconSize = 48;
    private const int GridPadding = 8;
    private const int IconTopPadding = 8;
    private const int CellWidth = 84;
    private const int CellHeight = 94;
    // SettingsButtonGap (the vertical gap between the button row's bottom edge and the fence's own
    // top edge) is LayeredWidgetForm's own default (6) now, unchanged from what this used to declare
    // itself - TopMargin's own extra room above OuterMargin is still sized for it.
    // Copy Fence/Delete Fence's own square footprint (see ExtraButtons below) - same height Settings
    // itself already uses.
    private const int SmallButtonSize = 22;

    private readonly FenceManager _manager;
    private readonly FenceModel _model;
    private readonly IDesktopAnchorStrategy _anchorStrategy;
    private readonly Dictionary<string, Icon?> _iconCache = new();
    private EditBox? _itemRenameBox;
    private ContextMenuStrip? _itemContextMenu;
    private string? _itemRenamePath;
    private string? _contextItem;
    private int _hoverIndex = -1;

    // Internal drag state for reordering/removing items - this is all local mouse tracking, not
    // OLE drag-and-drop (which is only for accepting drops from outside the app, via
    // OnDragEnter/OnDragDrop above). "Armed" means the mouse is down on an item but hasn't moved
    // far enough yet to count as a drag rather than a click.
    private const int DragThreshold = 4;
    private int? _dragArmIndex;
    private Point _dragArmPoint;
    private int? _draggingIndex;
    private Point _dragCurrentPoint;
    private DragGhostWindow? _dragGhost;

    // Vertical scroll for fences that hold more rows of items than fit in their set height - see
    // Scrollbar's own doc comment for why this is shared with LayeredWidgetForm's generic list.
    private readonly Scrollbar _scrollbar = new();

    // A real child Button control was tried here first, but a window painted via UpdateLayeredWindow
    // (see RenderAndPresent/LayeredWindowPresenter) doesn't compose child windows on top of itself -
    // it just never appeared, clickable or not. So this is drawn like everything else on the fence
    // (see PaintContent) and hit-tested by hand instead: armed on OnMouseDown, fired on the matching
    // OnMouseUp only if the cursor is still over it, mirroring the arm-then-fire pattern used for
    // drag-vs-click elsewhere in this file. Firing on down instead of up was tried too, early in
    // this button's history - opening the dropdown while the mouse button is still physically down
    // raced with TrackPopupMenuEx's own capture and made it flash open and closed.
    private bool _settingsButtonArmed;
    // Copy Fence/Delete Fence are LayeredWidgetForm's own ExtraButtons now (see the constructor) -
    // TryArmExtraButton/FireArmedExtraButton handle their own arm-then-fire and hover tooltip, so
    // there's no fence-owned arm flag or tooltip field left to keep here.

    // Whether the drag that's about to start is a resize (as opposed to a move) - LayeredWidgetForm's
    // own IsResizing now (set from OnNcLButtonDown's own base default); read back on OnDragEnd to
    // decide whether OcdFenceSizing should auto-run "Both" now that the resize is done.

    public Guid FenceId => _model.Id;

    /// <summary>Copy Fence (the same two-squares "duplicate" glyph Copy Settings itself used to use -
    /// see LayeredWidgetForm.PaintCopyIconGlyph, a much more literal fit for "duplicate" than the
    /// eyedropper that button has now)/Delete Fence ("x", crossed diagonals - see
    /// PaintDeleteFenceGlyph) - LayeredWidgetForm's own ChromeButton mechanism instead of this
    /// fence's former hand-rolled rect-chaining/paint/hit-test/arm-fire/tight-band code, with a
    /// custom PaintGlyph each since neither reads well as a single text character the way every
    /// other widget's own ChromeButtons do. Declared in this order (Copy Fence closest to Copy
    /// Settings, Delete Fence outermost) so on a narrowing fence Delete drops off the bar into the
    /// Settings dropdown first, Copy only once there's no longer room for either (see
    /// VisibleExtraButtonCount) - same reach either way, just relocated.</summary>
    protected override IReadOnlyList<ChromeButton> ExtraButtons { get; }

    /// <summary>Fired instead of adding the item, when a single folder gets dropped onto this fence
    /// while it's completely empty - see OnDragDrop. FenceManager forwards this up to
    /// TrayApplicationContext (the one place that already holds both FenceManager and
    /// FolderFenceManager - see FolderFenceManager's own constructor comment on why that dependency
    /// only runs one way), which actually performs the conversion into a Folder Fence.</summary>
    public event EventHandler<string>? FolderDroppedOnEmptyFence;

    /// <summary>Whether this fence currently holds no items at all - used by FolderFenceForm's own
    /// cross-fence item drag (see its own ComputeDragHint/OnMouseUp) to know a subfolder dropped
    /// here would convert this fence into a folder fence instead of just adding an ordinary
    /// shortcut - the same rule an OLE folder drop already follows (see IsFolderConversionDrop),
    /// just checked from outside this class since that drag never goes through OnDragDrop at all.</summary>
    internal bool IsEmpty => _model.Files.Count == 0;

    /// <summary>Runs OCD Fence Sizing's own fit-to-content once, immediately - same call
    /// ToggleOcdFenceSizing itself makes when turning the setting on, but for a fence created with
    /// OcdFenceSizing already true in its model (see FenceManager.AddRecycleBin), which otherwise
    /// wouldn't get tidied up until the next manual resize (see OnDragEnd). A no-op if the model
    /// doesn't actually have it on.</summary>
    public void ApplyOcdSizingIfEnabled()
    {
        if (_model.OcdFenceSizing)
            FormatDimensions(adjustWidth: true, adjustHeight: true);
    }

    /// <summary>Repositions this fence so its own bottom-right corner sits margin px inside the
    /// primary monitor's own working-area bottom-right corner - used by FenceManager.AddRecycleBin
    /// right after ApplyOcdSizingIfEnabled, once _model.Bounds already holds this fence's real
    /// OCD-fitted size, so the corner math uses the actual wrapped-tight size rather than a guessed
    /// placeholder one.</summary>
    public void MoveToBottomRight(int margin)
    {
        var workArea = Screen.PrimaryScreen!.WorkingArea;
        var x = workArea.Right - margin - _model.Bounds.Width;
        var y = workArea.Bottom - margin - _model.Bounds.Height;

        ButtonRowAtBottom = ComputeButtonRowAtBottom(new Point(x, y), TopMargin);
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, x - OuterMargin, y - TopBand, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        _model.X = x;
        _model.Y = y;
        _manager.Save();
    }

    /// <summary>Which model LayeredWidgetForm's own theme derivation (ThemedBody/Accent/etc) and
    /// generic Settings-dropdown rows (Hide Header, Full Opacity When Active, the color grid/sliders)
    /// read from - FenceModel already implements IWidgetStyle.</summary>
    protected override IWidgetStyle Style => _model;

    protected override bool HideHeader
    {
        get => _model.HideHeader;
        set
        {
            _model.HideHeader = value;
            _manager.Save();
            // Changes GridTop (see its own comment), which OCD Fence Sizing's fit is based on - only
            // height can possibly need to change here, never the columns/width.
            if (_model.OcdFenceSizing)
                FormatDimensions(adjustWidth: false, adjustHeight: true);
            RenderAndPresent();
        }
    }

    protected override bool ShowHeaderCloseButton
    {
        get => _model.HeaderCloseButton;
        set
        {
            _model.HeaderCloseButton = value;
            _manager.Save();
            RenderAndPresent();
        }
    }

    /// <summary>Used only for the cross-fence "Move to {name}" drag hint (see ComputeDragHint) -
    /// every other cross-fence reference goes through FenceId/FenceManager instead.</summary>
    internal string FenceName => _model.Name;

    /// <summary>Item cell height when labels are hidden (FenceModel.HideLabels) - just the icon
    /// plus a little breathing room, since there's no label text to make room for underneath.</summary>
    private int EffectiveCellHeight => _model.HideLabels ? IconTopPadding + IconSize + 8 : CellHeight;

    /// <summary>Where the item grid starts, content-relative - below the title bar normally, or
    /// right at the top when FenceModel.HideHeader reclaims that space entirely.</summary>
    private int GridTop => _model.HideHeader ? 0 : TitleBarHeight;

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

            ButtonRowAtBottom = ComputeButtonRowAtBottom(_model.Bounds.Location, TopMargin);

            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN;
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
            cp.X = _model.Bounds.X - OuterMargin;
            cp.Y = _model.Bounds.Y - TopBand;
            cp.Width = _model.Bounds.Width + OuterMargin * 2;
            cp.Height = _model.Bounds.Height + TopBand + BottomBand;
            return cp;
        }
    }

    public FenceForm(FenceModel model, FenceManager manager, IDesktopAnchorStrategy anchorStrategy)
        : base(model.Opacity / 100f, manager)
    {
        _model = model;
        _manager = manager;
        _anchorStrategy = anchorStrategy;

        ExtraButtons = new List<ChromeButton>
        {
            new("+", SmallButtonSize, () => _manager.CreateFenceLike(FenceId), "Copy Fence", PaintCopyIconGlyph),
            new("×", SmallButtonSize, ConfirmDelete, "Delete Fence", PaintDeleteFenceGlyph),
        };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;
        // LayeredWidgetForm's own default rename hit-testing/EditBox/title-context-menu (IsOverTitleRow,
        // BeginRename, BuildTitleContextMenu) all measure against Control.Font - without this, they'd
        // measure against the WinForms default (Microsoft Sans Serif) while PaintContent actually draws
        // with this same font instead. AppTheme.Font (Segoe UI 9) rather than a fence-owned instance -
        // no need to create (and remember to dispose) a private copy of a font every other themed
        // window in the app already shares.
        Font = AppTheme.Font;

        Reanchor();
        RenderAndPresent();
    }

    /// <summary>Where an item dropped at screenPoint (dragged in from a different fence, see
    /// FenceManager.MoveFileToFence) would land in this fence's own grid - appended to the end when
    /// the point isn't over a specific item (e.g. it's in the margin, or past the last row).</summary>
    internal int IndexForExternalDrop(Point screenPoint) =>
        IndexAtGridPosition(ToContent(PointToClient(screenPoint))) ?? _model.Files.Count;

    /// <summary>Repaints after FenceManager mutates this fence's model on behalf of a *different*
    /// fence's drag operation (see MoveFileToFence) - this fence's own drag/drop paths already
    /// re-render themselves directly.</summary>
    internal void RefreshAfterExternalChange() => RenderAndPresent();

    public new void Show() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);

    public void SetVisible(bool visible) =>
        NativeMethods.ShowWindow(Handle, visible ? NativeMethods.SW_SHOWNOACTIVATE : NativeMethods.SW_HIDE);

    /// <summary>Re-applies the desktop anchor (e.g. after explorer.exe restarts or a display
    /// change invalidates the previous z-order/parenting). Uses _model.Bounds (our own tracked
    /// absolute screen position), which is authoritative regardless of whatever coordinate
    /// convention the current native parent implies.</summary>
    public void Reanchor() => _anchorStrategy.Apply(Handle, _model.Bounds);

    // GetContentSize/ToContent/ToWindow/ToScreen are LayeredWidgetForm's own now - all grid/
    // hit-test math below is in "content" space (the visible fence's size minus OuterMargin on the
    // left/right/non-button-row side and TopBand/BottomBand's button-row side - see
    // ButtonRowAtBottom).

    private static int GetColumns(int contentWidth) => Math.Max(1, (contentWidth - GridPadding * 2) / CellWidth);

    /// <summary>How far the grid can scroll (0 if every item's row already fits in contentHeight).</summary>
    private int GetMaxScroll(int contentWidth, int contentHeight)
    {
        if (_model.Files.Count == 0)
            return 0;

        var columns = GetColumns(contentWidth);
        var rows = (_model.Files.Count + columns - 1) / columns;
        var availableHeight = Math.Max(0, contentHeight - GridTop - GridPadding * 2);
        return Math.Max(0, rows * EffectiveCellHeight - availableHeight);
    }

    /// <summary>The scrollbar's own viewport - Scrollbar.GetGeometry only reads Right/Top/Height off
    /// this (a scrollbar always hugs the right edge of whatever it's given), so Left/Width beyond
    /// contentWidth itself don't matter here.</summary>
    private Rectangle GridViewport(int contentWidth, int contentHeight)
    {
        var trackTop = GridTop + GridPadding;
        var trackHeight = Math.Max(0, contentHeight - trackTop - GridPadding);
        return new Rectangle(0, trackTop, contentWidth, trackHeight);
    }

    /// <summary>LayeredWidgetForm's own Dispose(bool) calls this (having already set IsDisposing=true
    /// and torn down the rename box/title menu/settings dropdown it now owns) before disposing
    /// RenderOpacity/the theme brush itself. Destroying the native window via DestroyWindow, as part
    /// of the OS's normal deactivate-before-destroy sequence, synchronously delivers WM_ACTIVATE to
    /// this same window while WndProc is still hooked up, reaching OnDeactivate -> RenderAndPresent ->
    /// PaintItems before this call even returns - without IsDisposing already set, that repaint would
    /// use _iconCache's Icon objects just disposed a few lines down, which throws (Icon is an
    /// ObjectDisposedException-checked handle, same as Control.Handle).</summary>
    protected override void DisposeOwnedResources()
    {
        _itemRenameBox?.Dispose();
        _itemContextMenu?.Dispose();
        _dragGhost?.Dispose();
        foreach (var icon in _iconCache.Values)
            icon?.Dispose();
    }

    // Activation (settings button + drag-margin visibility, see WidgetActivation) is intentionally
    // NOT driven by OnActivated - that fires for any click that gives the window OS focus, including
    // a plain click on a shortcut just to use it. It's set explicitly instead, only for right-click
    // (anywhere) or a title-bar click (either button) - see LayeredWidgetForm's own WM_NCLBUTTONDOWN/
    // WM_NCRBUTTONDOWN handling and ShowContextMenu. Resizing deliberately does NOT activate the
    // fence - HitTest turns the whole margin band into a move handle once already active, so resize
    // and move never contend for the same pixels, but that also means resize has to stay unavailable
    // to the (fence, click) pairs that would otherwise be ambiguous. Losing focus still deactivates
    // unconditionally - see LayeredWidgetForm's own OnDeactivate, which now handles that.

    protected override void OnDragEnter(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        // Link (rather than the plain Move every other drop onto this fence shows) whenever this
        // drop would turn into a Folder Fence conversion instead of an ordinary add - same cue
        // FolderFenceForm's own empty "+" state gives for the identical single-folder-onto-empty
        // case, so the cursor already hints at the different outcome before the drop lands.
        e.Effect = IsFolderConversionDrop(paths) ? DragDropEffects.Link : DragDropEffects.Move;
    }

    private bool IsFolderConversionDrop(string[] paths) =>
        _model.Files.Count == 0 && paths is { Length: 1 } && Directory.Exists(paths[0]);

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        if (IsFolderConversionDrop(paths))
        {
            FolderDroppedOnEmptyFence?.Invoke(this, paths[0]);
            return;
        }

        // e.X/e.Y are screen coordinates (unlike MouseEventArgs.Location) - PointToClient first to
        // land in the same window-relative space ToContent/IndexAtGridPosition expect elsewhere.
        var contentPoint = ToContent(PointToClient(new Point(e.X, e.Y)));
        if (IndexAtGridPosition(contentPoint) is int index && _manager.IsRecycleBinAt(FenceId, index))
            _manager.DeletePaths(paths, Handle);
        else
            _manager.AddFiles(FenceId, paths);
        RenderAndPresent();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        // Rename is only reachable via the title text itself (double-click or right-click, gated on
        // IsOverTitleRow - now LayeredWidgetForm's own) - no fallback here when FenceModel.HideHeader
        // leaves no title bar to click at all; renaming just isn't reachable that way then, rather
        // than an empty double-click anywhere substituting for it.
        if (IndexAtGridPosition(ToContent(e.Location)) is not int index)
            return;
        var item = _model.Files[index];
        // FenceItem.Path is the Recycle Bin's shell-namespace CLSID string for icon-extraction
        // purposes only (see FenceItem.IsRecycleBin) - opening it needs the "shell:" alias instead,
        // a different shell path grammar OpenItem's ShellExecute still resolves the same way.
        OpenItem(item.IsRecycleBin ? "shell:RecycleBinFolder" : item.Path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentSize = GetContentSize();
        var onLeft = ShouldSettingsButtonOpenLeft(contentSize.Width);

        if (TryArmHeaderCloseButton(contentPoint))
            return;

        if (ShowsButtons && GetSettingsButtonRect(contentSize.Width, onLeft).Contains(contentPoint))
        {
            _settingsButtonArmed = true;
            return;
        }

        if (ShowsButtons && TryArmExtraButton(contentPoint))
            return;

        if (_scrollbar.TryHandleMouseDown(contentPoint, GridViewport(contentSize.Width, contentSize.Height),
                GetMaxScroll(contentSize.Width, contentSize.Height), EffectiveCellHeight))
        {
            Capture = true;
            RenderAndPresent();
            return;
        }

        if (IndexAtGridPosition(contentPoint) is int index)
        {
            _dragArmIndex = index;
            _dragArmPoint = e.Location; // raw window-space is fine here - only ever used as a delta
        }

        // Moving now happens via the margin band outside the visible fence (see HitTest's move-ring
        // check) rather than by clicking empty content here - that band always exists, regardless of
        // whether there's a title bar or how densely packed the grid is, so there's no more fallback
        // needed at this layer.
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_scrollbar.IsDragging)
        {
            var contentSize = GetContentSize();
            var contentPoint = ToContent(e.Location);
            if (_scrollbar.UpdateDrag(contentPoint, GridViewport(contentSize.Width, contentSize.Height),
                    GetMaxScroll(contentSize.Width, contentSize.Height)))
                RenderAndPresent();
            return;
        }

        if (_draggingIndex is null && _dragArmIndex is int armIndex && MouseButtons == MouseButtons.Left)
        {
            var dx = e.X - _dragArmPoint.X;
            var dy = e.Y - _dragArmPoint.Y;
            if (dx * dx + dy * dy >= DragThreshold * DragThreshold)
            {
                _draggingIndex = armIndex;
                _dragArmIndex = null;
                Capture = true;

                var item = _model.Files[armIndex];
                _dragGhost = new DragGhostWindow(GetIcon(item.Path), GetDisplayName(item));
            }
        }

        if (_draggingIndex is not null)
        {
            _dragCurrentPoint = ToContent(e.Location);
            _dragGhost?.SetHint(ComputeDragHint(e.Location));
            _dragGhost?.MoveTo(PointToScreen(e.Location));
            RenderAndPresent();
            return;
        }

        SetHoverIndex(IndexAtGridPosition(ToContent(e.Location)) ?? -1);
        // Copy Fence/Delete Fence's own hover tint/tooltip are LayeredWidgetForm's own now (see
        // ExtraButtons) - base.OnMouseMove above already ran UpdateButtonHover, so there's nothing
        // left for this override to do for them.
    }

    /// <summary>Live drop-target hint for an in-app item drag (see _draggingIndex), shown in the
    /// pill below the drag ghost - mirrors the tooltip Windows itself shows while dragging a file
    /// over a folder or the desktop Recycle Bin icon. Mirrors OnMouseUp's own same-fence/cross-
    /// fence/neither resolution exactly (including the recycle-bin sub-case), just read-only (no
    /// mutation) and re-run on every mouse-move rather than only at drop time. Never returns a hint
    /// while the trash item itself is what's being dragged - same reasoning as OnMouseUp's own
    /// isSourceTrash guard, repositioning the trash icon onto its own cell is never a delete, and
    /// dragging it to another fence or off onto the desktop isn't really a "move"/"remove" either
    /// since it always stays exactly one Recycle Bin, just relocated.</summary>
    private string? ComputeDragHint(Point windowLocation)
    {
        if (_draggingIndex is not int sourceIndex || _model.Files[sourceIndex].IsRecycleBin)
            return null;

        var contentPoint = ToContent(windowLocation);
        if (new Rectangle(Point.Empty, GetContentSize()).Contains(contentPoint))
        {
            var index = IndexAtGridPosition(contentPoint) ?? _model.Files.Count;
            if (_manager.IsRecycleBinAt(FenceId, index))
                return "Move to Recycle Bin";
            // Landing back on (or adjacent to) its own starting cell isn't really a reorder, but
            // there's no cheap way to tell "would actually move" from "would land right back where
            // it started" here without duplicating MoveFile's own index-shift math - and showing the
            // hint a little early/late right at the source cell is harmless, unlike misreporting a
            // Recycle Bin/cross-fence target.
            return "Change Position";
        }

        var screenPoint = PointToScreen(windowLocation);
        if (_manager.FindFenceAt(screenPoint, FenceId) is { } targetForm)
        {
            var index = targetForm.IndexForExternalDrop(screenPoint);
            return _manager.IsRecycleBinAt(targetForm.FenceId, index)
                ? "Move to Recycle Bin"
                : $"Move to {targetForm.FenceName}";
        }

        return "Remove from Fence";
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        var onLeft = ShouldSettingsButtonOpenLeft(GetContentSize().Width);

        FireArmedHeaderCloseButton(ToContent(e.Location));

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            if (ShowsButtons && GetSettingsButtonRect(GetContentSize().Width, onLeft).Contains(ToContent(e.Location)))
                OpenSettingsMenu();
            return;
        }

        FireArmedExtraButton(ToContent(e.Location));

        if (_scrollbar.EndDrag())
        {
            Capture = false;
            return;
        }

        _dragArmIndex = null;
        if (_draggingIndex is not int sourceIndex)
            return;

        Capture = false;
        _draggingIndex = null;
        _dragGhost?.Dispose();
        _dragGhost = null;

        var contentPoint = ToContent(e.Location);
        var path = _model.Files[sourceIndex].Path;
        // The trash item being dragged (repositioned, or dropped back near its own cell) is never
        // itself "dropped onto the trash" - only some *other* item landing on the trash cell means
        // delete. Checked once up front rather than at each landing-spot branch below.
        var isSourceTrash = _model.Files[sourceIndex].IsRecycleBin;
        if (new Rectangle(Point.Empty, GetContentSize()).Contains(contentPoint))
        {
            var targetIndex = IndexAtGridPosition(contentPoint) ?? _model.Files.Count;
            if (!isSourceTrash && _manager.IsRecycleBinAt(FenceId, targetIndex))
                _manager.DeleteFencedItem(FenceId, path, Handle);
            else
                _manager.MoveFile(FenceId, path, targetIndex);
        }
        else
        {
            // Not a drop inside this fence's own content - check whether it landed on a *different*
            // fence's window instead of empty desktop, and hand the item over rather than discarding
            // it (the pre-existing behavior for a drop that lands nowhere).
            var screenPoint = PointToScreen(e.Location);
            if (_manager.FindFenceAt(screenPoint, FenceId) is { } targetForm)
            {
                var targetIndex = targetForm.IndexForExternalDrop(screenPoint);
                if (!isSourceTrash && _manager.IsRecycleBinAt(targetForm.FenceId, targetIndex))
                    _manager.DeleteFencedItem(FenceId, path, Handle);
                else
                    _manager.MoveFileToFence(FenceId, targetForm.FenceId, path, targetIndex);
            }
            else
                _manager.RemoveFile(FenceId, path);
        }

        RenderAndPresent();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        var contentSize = GetContentSize();
        var maxScroll = GetMaxScroll(contentSize.Width, contentSize.Height);
        if (_scrollbar.HandleWheel(e.Delta, EffectiveCellHeight, maxScroll))
            RenderAndPresent();
    }

    // OnMouseEnter needs no override of its own anymore - LayeredWidgetForm's own already does
    // exactly what this used to (track client-area hover, begin easing opacity, and - now that Copy
    // Fence/Delete Fence are ExtraButtons - hide their own hover tooltip too). OnMouseLeave still
    // needs one, just for this fence's own icon-grid hover below.
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoverIndex(-1);
    }

    private void SetHoverIndex(int index)
    {
        if (index == _hoverIndex)
            return;
        _hoverIndex = index;
        RenderAndPresent();
    }

    /// <summary>WM_PAINT/WM_ERASEBKGND swallowing (a layered window is never repainted by Windows
    /// itself - see RenderAndPresent) and the item-rename context menu's own message plumbing are
    /// both LayeredWidgetForm's own now - the former because it's universal to any subclass on this
    /// base, not fence-specific at all; the latter because the item-rename menu switched from a
    /// native TrackPopupMenuEx (its own WM_COMMAND/WM_MEASUREITEM/WM_DRAWITEM machinery) to a plain
    /// ContextMenuStrip, the same mechanism the base's own title-rename menu already uses (see
    /// ShowItemContextMenu). WM_DISPLAYCHANGE/WM_DPICHANGED are the only messages left with no
    /// shared home.</summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == NativeMethods.WM_DISPLAYCHANGE || m.Msg == NativeMethods.WM_DPICHANGED)
            Reanchor();
    }

    /// <summary>The fixed anchor LayeredWidgetForm's own WM_MOVING/WM_SIZING measure every tick
    /// against - _model.Bounds itself, same as always.</summary>
    protected override Rectangle GetCurrentBody() => _model.Bounds;

    protected override int SnapMargin => _model.Margin;

    // ComputeMovedBody/ComputeResizedBody/BeginSnapDrag all use LayeredWidgetForm's own defaults
    // unchanged - this fence's own snapping (against every other live widget's edges - other fences,
    // the Layout Launcher, any future widget - and custom snap lines) is exactly what those defaults
    // already do via GetOtherWidgetEdges/SnapMargin/Fences above.

    protected override void OnDragEnd()
    {
        if (NativeMethods.GetWindowRect(Handle, out var rect))
        {
            _model.Bounds = Rectangle.FromLTRB(
                rect.Left + OuterMargin, rect.Top + TopBand, rect.Right - OuterMargin, rect.Bottom - BottomBand);
            _manager.Save();
        }

        // OCD Fence Sizing: snap to the tightest fit right after a manual resize, on top of
        // whatever size was just dragged to - not after a move, see IsResizing. Done before the
        // HWND_BOTTOM restack below (rather than after) so that restack is always the last
        // z-order-relevant call in this handler - FormatDimensions makes its own SetWindowPos call
        // (SWP_NOZORDER, meant to leave z-order untouched), but a resize followed by a move was
        // still landing behind other fences with the restack first, so the z-order push now
        // unconditionally comes last regardless of what ran before it.
        if (IsResizing && _model.OcdFenceSizing)
            FormatDimensions(adjustWidth: true, adjustHeight: true);

        // Dragging a fence via its caption goes through the OS's own window-move loop, which
        // activates it like any normal window drag would - left alone, it'd then stay stacked on
        // top of whatever window it was just dragged over, contradicting the whole point of a fence
        // (a desktop-level widget that never covers what you're actually working in). Dropping it
        // to the bottom of the z-order here restores that even though it was just OS-activated;
        // SWP_NOACTIVATE keeps this restack itself from stealing focus back.
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        // HWND_BOTTOM above means "underneath literally everything, including every other fence" -
        // left at just that, an actively-dragged (and still visually highlighted, see
        // ShowsButtons/ThemedActiveBorder) fence would disappear behind any other fence it
        // overlaps. Pushing every OTHER fence to the same HWND_BOTTOM afterward settles this one
        // back on top of its siblings without ever elevating any fence above a real window - see
        // RestackOtherFencesBehind's own comment for why order-of-calls is what makes that work.
        _manager.RestackOtherFencesBehind(FenceId);

        // RenderOpacity.BeginIfNeeded() and the Settings-button-corner repaint a pure move otherwise
        // needs (see ShouldSettingsButtonOpenLeft) are both LayeredWidgetForm's own now - it calls
        // both right after this method returns.
    }

    // OnResized needs no override of its own - LayeredWidgetForm's own default (repositioning an
    // already-open Settings dropdown after a resize) already covers the OCD flyout's own resize
    // commands (FormatDimensions), the only thing that used to need this.

    protected override int HitTest(IntPtr lParam)
    {
        var rectPoint = lParam;
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
            return HTCLIENT;

        var windowPoint = ScreenLParamToWindowPoint(rectPoint, rect);
        int x = windowPoint.X;
        int y = windowPoint.Y;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        // The settings button (and the "+"/"x" buttons beside it) live above the fence, in the taller
        // TopMargin band - check them first so none is shadowed by a resize-band result.
        var contentWidth = width - OuterMargin * 2;
        var contentPoint = ToContent(windowPoint);
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        if (ShowsButtons && (GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint)
            || TryGetExtraButtonAt(contentWidth, onLeft, contentPoint, out _)))
            return HTCLIENT;

        // Not gated by ShowsButtons, unlike every check above - see IsOverHeaderCloseButton's own
        // comment.
        if (IsOverHeaderCloseButton(contentPoint))
            return HTCLIENT;

        if (ShowsButtons)
        {
            // ShowsButtons (IsActive || MenuOpen), not just whether this fence is the active
            // window - opening the settings dropdown steals OS activation from the fence (it's a
            // separate top-level Form), which deactivates it via OnDeactivate even though the
            // button/active border deliberately stay showing (see ShowsButtons's own
            // comment). Gating on plain activation alone let the resize hit-test codes below fire
            // while the dropdown was still open, so dragging an edge resized the fence out from
            // under its own still-open menu.
            //
            // The margin band is a move handle instead of a resize band while active - the same
            // footprint resize used to claim, just reassigned rather than split into two adjacent
            // rings, so the drag margin can hug the fence's actual edge (see PaintContent's own
            // ThemedActiveBorder highlight) without an ambiguous strip where both would apply.
            // Resizing an active fence isn't available until it's deactivated again.
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

        // Empty space within the title bar itself (content-relative, not the margin above), for a
        // fence that still has one - HTBORDER, not HTCAPTION, so a left-button drag from here no
        // longer moves the fence (only the margin does, and only once already active - see the
        // ShowsButtons branch above). Right-click/double-click (rename) and hover still work
        // the same as any other non-client area - see HTBORDER's own comment.
        if (!_model.HideHeader && y - TopBand <= TitleBarHeight)
            return HTBORDER;

        return HTCLIENT;
    }

    /// <summary>
    /// Everything LayeredWidgetForm's own RenderAndPresent doesn't already handle: body, title bar,
    /// Settings/"+"/"x" buttons, and this fence's own items (see PaintItems).
    /// </summary>
    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        _scrollbar.ClampToMax(GetMaxScroll(contentWidth, contentHeight));

        // Body/title fill, border, title text, the Settings button, and Copy Fence/Delete Fence
        // (LayeredWidgetForm's own ExtraButtons now - see PaintExtraButtons/ChromeButton.PaintGlyph)
        // are all LayeredWidgetForm's own now - this only draws what's genuinely fence-specific: the
        // item grid (see PaintItems).
        PaintChrome(g, contentWidth, contentHeight);
        PaintItems(g, contentWidth, contentHeight);
    }

    /// <summary>Delete Fence's own glyph (see ExtraButtons) - the "x" glyph itself already reads as
    /// destructive without needing a separate warning color too, same reasoning as this fence's own
    /// trash-cell drop handling elsewhere.</summary>
    private static void PaintDeleteFenceGlyph(Graphics g, Rectangle rect)
    {
        using var xPen = new Pen(Color.WhiteSmoke, 1.6f);
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        const float half = 4.5f;
        g.DrawLine(xPen, cx - half, cy - half, cx + half, cy + half);
        g.DrawLine(xPen, cx - half, cy + half, cx + half, cy - half);
    }

    /// <summary>
    /// Draws this fence's own icon+label for each file it holds, in a simple grid below the title
    /// bar - a real desktop file's own icon is moved into a hidden folder while it's fenced (see
    /// FenceManager.AddFiles), so this is the only place it's actually represented on screen.
    /// </summary>
    private void PaintItems(Graphics g, int width, int height)
    {
        if (_model.Files.Count == 0)
            return;

        // Items scrolled above the grid top or below the fence's bottom edge must not be able to
        // paint there - see the SetClip comment above for why that's not just a visibility issue.
        g.SetClip(ToWindow(new Rectangle(0, GridTop, width, height - GridTop)), CombineMode.Intersect);

        var columns = GetColumns(width);

        for (int i = 0; i < _model.Files.Count; i++)
        {
            var item = _model.Files[i];
            var isDragSource = i == _draggingIndex;
            var column = i % columns;
            var row = i / columns;
            var cellX = GridPadding + column * CellWidth;
            var cellY = GridTop + GridPadding + row * EffectiveCellHeight - _scrollbar.Offset;

            // A scrolled row can straddle the grid-top boundary - skip painting one entirely once
            // it's fully off either edge, rather than relying on g.Clip alone to hide it.
            if (cellY + EffectiveCellHeight <= GridTop || cellY >= height)
                continue;

            if (i == _hoverIndex && !isDragSource)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
                using var hoverRect = RoundedRect(ToWindow(new Rectangle(cellX, cellY, CellWidth, EffectiveCellHeight)), 4);
                g.FillPath(hoverBrush, hoverRect);
            }

            var icon = GetIcon(item.Path);
            if (icon is not null)
            {
                var iconX = cellX + (CellWidth - IconSize) / 2;
                using var bitmap = icon.ToBitmap();
                var iconRect = ToWindow(new Rectangle(iconX, cellY + IconTopPadding, IconSize, IconSize));
                // Faded in place while its being dragged - the ghost near the cursor (painted
                // after the grid, see PaintDragFeedback) is what's actually "held".
                if (isDragSource)
                    DrawImageWithOpacity(g, bitmap, iconRect, 0.35f);
                else
                    g.DrawImage(bitmap, iconRect);
            }

            if (item.Path == _itemRenamePath || _model.HideLabels)
                continue;

            var labelTop = cellY + IconTopPadding + IconSize + 2;
            var labelHeight = CellHeight - IconTopPadding - IconSize - 2;
            if (labelTop >= GridTop)
            {
                // Only the bottom can need trimming here (the top is already in bounds).
                var visibleHeight = Math.Min(labelHeight, height - labelTop);
                if (visibleHeight > 0)
                {
                    var labelRect = ToWindow(new Rectangle(cellX, labelTop, CellWidth, visibleHeight));
                    // GDI+'s DrawString instead of GDI's TextRenderer.DrawText - see
                    // LayeredWidgetForm.PaintChrome's own title text for why (ClearType fringing,
                    // plus TextRenderer ignoring Graphics.Transform under RenderAndPresent's
                    // supersampling).
                    var previousTextHint = g.TextRenderingHint;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    using (var textBrush = new SolidBrush(Color.WhiteSmoke))
                    using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit })
                        g.DrawString(GetDisplayName(item), Font, textBrush, labelRect, textFormat);
                    g.TextRenderingHint = previousTextHint;
                }
            }
        }

        PaintDragFeedback(g, width, height);
        PaintScrollbar(g, width, height);
    }

    private void PaintScrollbar(Graphics g, int width, int height)
    {
        var maxScroll = GetMaxScroll(width, height);
        if (_scrollbar.GetGeometry(GridViewport(width, height), maxScroll) is { } sb)
            PaintScrollbar(g, sb);
    }

    /// <summary>Draws the drop-target outline while an in-progress item drag (started in
    /// OnMouseDown/OnMouseMove) is over this fence. The dragged item's own ghost is a separate
    /// floating window (DragGhostWindow) that follows the cursor, not drawn here.</summary>
    private void PaintDragFeedback(Graphics g, int width, int height)
    {
        if (_draggingIndex is null)
            return;

        if (!new Rectangle(0, 0, width, height).Contains(_dragCurrentPoint) ||
            IndexAtGridPosition(_dragCurrentPoint) is not int targetIndex)
            return;

        var columns = GetColumns(width);
        var cellX = GridPadding + targetIndex % columns * CellWidth;
        var cellY = GridTop + GridPadding + targetIndex / columns * EffectiveCellHeight - _scrollbar.Offset;

        using var targetPen = new Pen(Color.FromArgb(200, Accent), 2);
        using var targetRect = RoundedRect(ToWindow(new Rectangle(cellX + 1, cellY + 1, CellWidth - 2, EffectiveCellHeight - 2)), 4);
        g.DrawPath(targetPen, targetRect);
    }

    private static void DrawImageWithOpacity(Graphics g, Image image, Rectangle rect, float opacity)
    {
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity }, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        g.DrawImage(image, rect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>An explicit rename (set via the item's context menu) always wins; otherwise every
    /// item displays without its extension, for now - regardless of type.</summary>
    private static string GetDisplayName(FenceItem item) =>
        !string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : Path.GetFileNameWithoutExtension(item.Path);

    private Icon? GetIcon(string path)
    {
        // Only successes are cached - a failed extraction isn't necessarily permanent (e.g. the
        // file was mid-move via DesktopIconHider, or briefly locked by another process), and
        // caching null here would otherwise wedge the item icon-less for the rest of this fence's
        // lifetime even once the file becomes readable again. Every repaint just retries instead.
        if (_iconCache.TryGetValue(path, out var cached))
            return cached;

        Icon? icon = null;
        try
        {
            // The Recycle Bin's shell-namespace CLSID string (FenceItem.IsRecycleBin) isn't a real
            // path - ExtractLargeIcon's path-based SHGetFileInfo call doesn't resolve it, so this
            // needs the special-folder-PIDL route instead. Icon.ExtractAssociatedIcon would just
            // throw for it too (already caught below), so it's skipped entirely for this path.
            //
            // The shell's large image list gives a genuinely high-resolution icon (crisp at
            // IconSize) rather than the ~32px one Icon.ExtractAssociatedIcon returns, which looks
            // blurry once drawn at a larger size - only fall back to it if the shell lookup fails.
            icon = path == FenceManager.RecycleBinPath
                ? ShellIcons.ExtractRecycleBinIcon()
                : ShellIcons.ExtractLargeIcon(path) ?? Icon.ExtractAssociatedIcon(path);
        }
        catch (IOException)
        {
            // File may have been moved/deleted since it was dropped here.
        }
        catch (System.Security.SecurityException)
        {
        }

        if (icon is not null)
            _iconCache[path] = icon;
        return icon;
    }

    /// <summary>contentLocation is relative to the visible fence (see ToContent), not the padded window.</summary>
    private string? FileAtGridPosition(Point contentLocation)
    {
        var index = IndexAtGridPosition(contentLocation);
        return index is int i ? _model.Files[i].Path : null;
    }

    /// <summary>Like FileAtGridPosition, but only matches within the item's own label text - not
    /// its icon or the rest of the cell - and never matches at all when FenceModel.HideLabels has
    /// hidden every label. Used to gate right-click-to-rename (see ShowContextMenu) to specifically
    /// the shortcut name, matching the label rect PaintItems actually draws text into.</summary>
    private string? FileAtLabelPosition(Point contentLocation)
    {
        if (_model.HideLabels)
            return null;

        var index = IndexAtGridPosition(contentLocation);
        if (index is not int i)
            return null;

        var columns = GetColumns(GetContentSize().Width);
        var row = i / columns;
        var cellY = GridTop + GridPadding + row * EffectiveCellHeight - _scrollbar.Offset;
        var labelTop = cellY + IconTopPadding + IconSize + 2;
        return contentLocation.Y >= labelTop ? _model.Files[i].Path : null;
    }

    private int? IndexAtGridPosition(Point contentLocation)
    {
        if (_model.Files.Count == 0 || contentLocation.Y < GridTop)
            return null;

        var columns = GetColumns(GetContentSize().Width);

        var column = (contentLocation.X - GridPadding) / CellWidth;
        var row = (contentLocation.Y - GridTop - GridPadding + _scrollbar.Offset) / EffectiveCellHeight;
        if (column < 0 || column >= columns || row < 0)
            return null;

        var index = row * columns + column;
        return index >= 0 && index < _model.Files.Count ? index : null;
    }

    /// <summary>LayeredWidgetForm's own WM_RBUTTONUP handling has already activated the fence by the
    /// time this runs (see its own comment) - only a right-click on an item's label text specifically
    /// (see FileAtLabelPosition) has anything further to show. Not its icon, not empty grid space.
    /// Fence-level actions live elsewhere now: Rename only on the header (see LayeredWidgetForm's own
    /// title-rename) and Delete Fence only as the "x" button next to Settings (see
    /// GetDeleteButtonRect/ConfirmDelete) - a right-click anywhere else has nothing of its own to
    /// offer, so it just activates the fence without popping up a menu. Open and Remove From Fence
    /// used to live here too; both stayed reachable another way (double-click, drag off the fence) so
    /// removing them from this menu didn't remove the functionality, just this shortcut to it.</summary>
    protected override void OnClientRightClick(Point contentPoint)
    {
        _contextItem = FileAtLabelPosition(contentPoint);
        if (_contextItem is null)
            return;
        ShowItemContextMenu();
    }

    /// <summary>Same ContextMenuStrip-based pattern as LayeredWidgetForm's own title-rename menu (see
    /// its BuildTitleContextMenu) - lazily built, themed via TrayMenuRenderer using the same
    /// ChromeMenuFieldColor/ChromeMenuHoverColor colors, shown at the cursor. Used to be a hand-rolled
    /// native TrackPopupMenuEx with its own owner-draw WM_MEASUREITEM/WM_DRAWITEM handling just to
    /// show this one "Rename" item - all of that was redundant with a mechanism the base already
    /// provides for the exact same kind of menu.</summary>
    private void ShowItemContextMenu()
    {
        _itemContextMenu ??= BuildItemContextMenu();
        NativeMethods.GetCursorPos(out var pt);
        _itemContextMenu.Show(this, PointToClient(new Point(pt.X, pt.Y)));
    }

    private ContextMenuStrip BuildItemContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(() => ChromeMenuFieldColor, () => ChromeMenuHoverColor, () => AppTheme.Text),
            Font = Font,
        };
        menu.Items.Add("Rename", null, (_, _) => BeginRenameItem(_contextItem));
        return menu;
    }

    // Title/TitleRowHeight below are the only rename-related hooks left with a fence-specific
    // answer - TitleVisible (derived from HideHeader), ChromeMenuFieldColor/HoverColor, and
    // EditBoxTextColor/BackgroundColor are all LayeredWidgetForm's own defaults now (ChromeFill/
    // ThemedMenuSelected/ThemedBody, exactly what this used to override them to).

    protected override string Title
    {
        get => _model.Name;
        set
        {
            _model.Name = value;
            _manager.Save();
        }
    }

    protected override int TitleRowHeight => TitleBarHeight;

    /// <summary>Everything genuinely unique to a fence, not shared with any other widget on this
    /// base - shown in the "Additional" flyout LayeredWidgetForm's own default BuildSettingsRows adds
    /// below the menu's own top-level rows when this returns non-empty. Hide Header/Full Opacity When
    /// Active/the color grid/sliders/Corner Radius/Margin are all the base's own default rows now -
    /// this fence doesn't need to (and no longer does) rebuild the whole row list just to add these
    /// two. Copy Fence/Delete Fence are ExtraButtons now (see the constructor) - once this bar can't
    /// fit them, LayeredWidgetForm's own BuildSettingsRows relocates them to the very top of the menu
    /// itself, above "Base", not here. "Rename" still only ever lives in the header's own context
    /// menu, regardless of width.</summary>
    protected override IReadOnlyList<DropdownMenu.Row>? BuildAdditionalSettingsRows()
    {
        var rows = new List<DropdownMenu.Row>();

        // Copy Fence/Delete Fence's own tight-band fallback is LayeredWidgetForm's own now (they're
        // ExtraButtons - see BuildOverflowButtonRows), not a row inserted here.
        rows.Add(new DropdownMenu.Row(CmdToggleHideLabels, "Hide Shortcut Names", HasCheckbox: true, IsChecked: () => _model.HideLabels));
        rows.Add(new DropdownMenu.Row(CmdToggleOcdSizing, "OCD Fence Sizing", HasCheckbox: true, IsChecked: () => _model.OcdFenceSizing,
            Tooltip: GetMenuTooltipText(CmdToggleOcdSizing)));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        // A nested flyout instead of an inline "Fence Dimensions" header/group (see
        // DropdownMenu.Row.Submenu) - one fewer always-visible row, and "OCD" doubles as a nod to
        // "OCD Fence Sizing" just above it.
        rows.Add(new DropdownMenu.Row(0, "OCD", Submenu: new List<DropdownMenu.Row>
        {
            new(TagFenceDimensionsHeader, "Fence Dimensions", IsHeader: true),
            new(0, string.Empty, IsSeparator: true),
            new(CmdResizeBoth, "Both"),
            new(CmdResizeLeftRight, "Left/Right"),
            new(CmdResizeTopDown, "Top/Down"),
        }));

        return rows;
    }

    // SettingsMenuFieldColor/HoverColor/AccentColor/BorderColor/TooltipColor and the dropdown's own
    // reposition-on-resize are all LayeredWidgetForm's own defaults now (ChromeFill/ThemedMenuSelected/
    // Accent/ThemedCheckboxBorder, and OnResized - exactly what this used to override them to).

    // ColorRef/Tint/DarkenTowardBlack/SafeChromeBlend are all LayeredWidgetForm's own now.

    /// <summary>Only rows worth explaining get one - most menu items are self-explanatory from
    /// their label alone.</summary>
    private static string? GetMenuTooltipText(int commandId) => commandId switch
    {
        CmdToggleOcdSizing =>
            "After you resize this fence by hand, automatically snap it to the tightest size that fits its icons (same as OCD Formatting > Both).",
        _ => null,
    };

    /// <summary>Dispatches a clicked Settings-dropdown row id - only this fence's own additional-rows
    /// ids (see BuildAdditionalSettingsRows) need handling here; everything else (Hide Header, Full
    /// Opacity, the color/sliders/Corner Radius/Margin rows) is LayeredWidgetForm's own default
    /// row set, so falls through to its own HandleSettingsCommand - which still ends up calling this
    /// fence's own SetTintColor/SetOpacity/etc. overrides via ordinary virtual dispatch.</summary>
    protected override void HandleSettingsCommand(int id)
    {
        switch (id)
        {
            case CmdToggleHideLabels: ToggleHideLabels(); break;
            case CmdResizeBoth: FormatDimensions(adjustWidth: true, adjustHeight: true); break;
            case CmdResizeLeftRight: FormatDimensions(adjustWidth: true, adjustHeight: false); break;
            case CmdResizeTopDown: FormatDimensions(adjustWidth: false, adjustHeight: true); break;
            case CmdToggleOcdSizing: ToggleOcdFenceSizing(); break;
            default:
                base.HandleSettingsCommand(id);
                break;
        }
    }

    /// <summary>LayeredWidgetForm's own single required style-persistence hook - every IWidgetStyle
    /// property (color, Header Darkness, Opacity, Full Opacity When Active, Tint Strength, Margin,
    /// Corner Radius, Font Size, Align) is mutated directly against Style (== _model, the same
    /// instance FenceManager's own _models list already holds) by the base itself, so this fence
    /// doesn't need - and no longer has - a dedicated SetHeaderDarkness/SetOpacity/etc. override of
    /// its own for any of them; Save() just flushes whatever the base already changed.</summary>
    protected override void PersistStyle() => _manager.Save();

    /// <summary>Copy Settings To, fence-to-fence only (see LayeredWidgetForm.CopySettingsFrom's own
    /// same-type check) - Hide Shortcut Names/OCD Fence Sizing, the two settings genuinely specific
    /// to a fence (see BuildAdditionalSettingsRows above). Deliberately doesn't touch Files/Name/
    /// Bounds - same "settings, not identity/content" scope CopySettingsFrom itself already draws.</summary>
    protected override void CopyAdditionalSettingsFrom(LayeredWidgetForm source)
    {
        if (source is not FenceForm other)
            return;
        _model.HideLabels = other._model.HideLabels;
        _model.OcdFenceSizing = other._model.OcdFenceSizing;
    }

    private void OpenItem(string? path)
    {
        if (path is null)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The file may have been moved/deleted since it was dropped here - nothing to do.
        }
    }

    private void ToggleHideLabels()
    {
        _model.HideLabels = !_model.HideLabels;
        _manager.Save();
        // Changes EffectiveCellHeight (see its own comment), which OCD Fence Sizing's fit is based
        // on - only height can possibly need to change here, never the columns/width.
        if (_model.OcdFenceSizing)
            FormatDimensions(adjustWidth: false, adjustHeight: true);
        RenderAndPresent();
    }

    private void ToggleOcdFenceSizing()
    {
        _model.OcdFenceSizing = !_model.OcdFenceSizing;
        _manager.Save();
        // Otherwise this only ever takes effect after the next manual resize (see OnDragEnd) -
        // turning it on should tidy up the fence right away instead of waiting for that.
        if (_model.OcdFenceSizing)
            FormatDimensions(adjustWidth: true, adjustHeight: true);
        RenderAndPresent();
    }

    /// <summary>"OCD Formatting -> Fence Dimensions" - shrinks or grows the fence to trim away
    /// wasted space around its current grid, keeping the top-left corner fixed. Trims to what's
    /// already on screen, not the fence's full contents: height never expands past however many
    /// rows are currently visible, so a fence that's deliberately kept short (scrollable) doesn't
    /// get blown open to reveal everything. adjustWidth/adjustHeight let the three menu entries
    /// (Both/Left-Right/Top-Down) share this one implementation.</summary>
    private void FormatDimensions(bool adjustWidth, bool adjustHeight)
    {
        var contentSize = GetContentSize();
        if (contentSize.Width <= 0 || contentSize.Height <= 0 || _model.Files.Count == 0)
            return;

        var currentColumns = GetColumns(contentSize.Width);
        // Don't keep more column slots than there are icons to fill them - a fence with 2 icons
        // and room for 5 columns is just as untidy as one with extra trailing padding.
        var columns = adjustWidth ? Math.Min(currentColumns, _model.Files.Count) : currentColumns;

        var availableHeight = Math.Max(0, contentSize.Height - GridTop - GridPadding * 2);
        // Rounds to the nearest row rather than always truncating down to whatever's fully visible -
        // adding half a row's height before the integer division means a row that's more than half
        // shown counts as shown (the fence grows/keeps enough height for it), not cut off.
        var currentVisibleRows = Math.Max(1, (availableHeight + EffectiveCellHeight / 2) / EffectiveCellHeight);
        var totalRowsNeeded = (_model.Files.Count + columns - 1) / columns;
        var finalRows = adjustHeight ? Math.Min(currentVisibleRows, totalRowsNeeded) : currentVisibleRows;

        var newBounds = _model.Bounds;

        if (adjustWidth)
        {
            newBounds.Width = GridPadding * 2 + columns * CellWidth;

            // A fence that still won't show every row after this needs its own reserved strip for
            // the scrollbar - GridPadding is just breathing room around the grid, not real estate
            // set aside for it, so without this the scrollbar would have nowhere to go but
            // overlapping the last column's icons.
            if (finalRows < totalRowsNeeded)
                newBounds.Width += Scrollbar.Width + Scrollbar.Margin;
        }

        if (adjustHeight)
            newBounds.Height = GridTop + GridPadding * 2 + finalRows * EffectiveCellHeight;

        if (newBounds == _model.Bounds)
            return;

        // WM_SIZE (already handled in WndProc) re-renders with the new size once this returns - just
        // needs persisting, the same way OnDragEnd does after an interactive drag-resize.
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0,
            newBounds.Width + OuterMargin * 2, newBounds.Height + TopBand + BottomBand,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        _model.Bounds = newBounds;
        _manager.Save();
    }

    private void BeginRenameItem(string? path)
    {
        if (path is null || _itemRenameBox is not null)
            return;

        var index = _model.Files.FindIndex(f => f.Path == path);
        var contentSize = GetContentSize();
        if (index < 0 || contentSize.Width <= 0)
            return;

        var columns = GetColumns(contentSize.Width);
        var column = index % columns;
        var row = index / columns;
        var cellX = GridPadding + column * CellWidth;
        var absoluteCellY = GridTop + GridPadding + row * EffectiveCellHeight;

        // Scroll the item's row fully into view first if it's currently scrolled off - otherwise
        // the edit box could end up positioned above the grid top or below the fence entirely.
        var gridTop = GridTop + GridPadding;
        var gridBottom = contentSize.Height - GridPadding;
        if (absoluteCellY - _scrollbar.Offset < gridTop)
            _scrollbar.Offset = Math.Max(0, absoluteCellY - gridTop);
        else if (absoluteCellY + EffectiveCellHeight - _scrollbar.Offset > gridBottom)
            _scrollbar.Offset = Math.Min(GetMaxScroll(contentSize.Width, contentSize.Height), absoluteCellY + EffectiveCellHeight - gridBottom);

        var cellY = absoluteCellY - _scrollbar.Offset;
        var labelRect = ToWindow(new Rectangle(cellX, cellY + IconTopPadding + IconSize + 2, CellWidth, 20));

        _itemRenamePath = path;
        _itemRenameBox = new EditBox(Handle, GetDisplayName(_model.Files[index]), ToScreen(labelRect), Font);
        _itemRenameBox.Commit += OnItemRenameCommit;
        _itemRenameBox.Cancel += OnItemRenameCancel;
        RenderAndPresent();
    }

    private void OnItemRenameCommit(string newName)
    {
        _itemRenameBox?.Dispose();
        _itemRenameBox = null;
        var path = _itemRenamePath;
        _itemRenamePath = null;

        newName = newName.Trim();
        if (!string.IsNullOrEmpty(newName) && path is not null)
            _manager.RenameFile(FenceId, path, newName);

        RenderAndPresent();
    }

    private void OnItemRenameCancel()
    {
        _itemRenameBox?.Dispose();
        _itemRenameBox = null;
        _itemRenamePath = null;
        RenderAndPresent();
    }

    private void ConfirmDelete()
    {
        var result = MessageBox.Show(this,
            $"Delete fence \"{_model.Name}\"? The files inside it won't be deleted.",
            "Delete Fence", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
            _manager.DeleteFence(FenceId);
    }

    /// <summary>Routes the header close button (see ShowHeaderCloseButton/LayeredWidgetForm's own
    /// OnHeaderCloseButtonClick doc comment for why the base's plain Close() isn't safe here) through
    /// this fence's own already-confirmed delete flow instead - the same "×" Delete button uses, and
    /// the only "close" concept a fence actually has, since it has no per-fence hide state of its
    /// own.</summary>
    protected override void OnHeaderCloseButtonClick() => ConfirmDelete();

    // Thin wrapper kept under this file's own name/call sites rather than switching every one of
    // them to RoundedRectPath.Full directly - same behavior, smaller diff.
    private static GraphicsPath RoundedRect(Rectangle bounds, int radius) => RoundedRectPath.Full(bounds, radius);
}
