using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Fences.Native;
using DesktopTool.Features.Fences.UI;
using DesktopTool.Features.Snapping;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.FolderFences.UI;

/// <summary>
/// A fence-shaped widget that live-mirrors one real folder on disk instead of holding a
/// user-managed list of shortcuts (see FenceForm for that). Starts empty (a centered "+"); once
/// pointed at a folder (via that "+" or a folder dropped onto it), it shows that folder's own
/// top-level contents, read fresh off disk rather than stored in FolderFenceModel - a
/// FileSystemWatcher keeps the grid in sync with the real folder while it's open. Clicking into a
/// subfolder browses into it in place; the header shows a breadcrumb of however deep that's gone
/// (see DisplayTitle), and a back button to its own left (see PaintBackButton/BackButtonRect) goes
/// back up a level. Nothing here is reorderable or renameable the way FenceForm's own items are,
/// since there's no per-item state of this widget's own to reorder/rename in the first place. A
/// grid item drags out as a real file (see RunItemDrag), so it behaves exactly like one dragged
/// from Explorer: onto an ordinary fence it adds a reference (see FenceManager.AddFiles); onto
/// Explorer, the desktop, or a sub-folder tile / populated folder fence it's moved or copied for
/// real (see OnDragDrop); and a subfolder onto a still-empty fence or folder fence converts or
/// connects it.
///
/// Move, resize, snap, rename, and the Settings button/dropdown are all LayeredWidgetForm's own -
/// this class only supplies the small hooks those need plus everything genuinely specific to a
/// folder fence: the item grid, the empty-state "+", folder browsing, and the live watcher.
/// </summary>
internal sealed class FolderFenceForm : LayeredWidgetForm
{
    internal const int TitleBarHeight = 26;
    private const int OuterMarginPx = 13;
    // Same reasoning/value as FenceForm's own SettingsButtonOverhang - room for the button row
    // above the fence body.
    private const int SettingsButtonOverhang = 19;
    private const int TopMargin = OuterMarginPx + SettingsButtonOverhang;
    // Extra room reserved above TopMargin, purely for the folder tab (see PaintFolderTab) to poke
    // up into - the header itself stays a full, uncut strip (GetHeaderFillPath/GetBodyOutlinePath
    // aren't overridden at all), with the tab painted as its own separate shape sitting flush on
    // top of it, in space the header/body fill never reaches.
    private const int TabExtraHeight = 16;
    // The button row's own default position is the BOTTOM band now (see ComputeButtonRowAtBottomFor),
    // not the top - the tab needs its own reserved space up top regardless of where the button row
    // is, so TopBand is always at least this (tab alone) even in the default state, growing to
    // TopMarginWithTab (tab + button together) only once the button row itself flips up there.
    private const int TopMarginTabOnly = OuterMarginPx + TabExtraHeight;
    private const int TopMarginWithTab = TopMargin + TabExtraHeight;

    private const int IconSize = 48;
    private const int GridPadding = 8;
    private const int IconTopPadding = 8;
    private const int CellWidth = 84;
    private const int CellHeight = 94;
    // Smaller than a regular item's own IconSize (48) - it's a placeholder glyph, not a real icon,
    // so it shouldn't visually outweigh one. Shrinks further still (see EmptyStatePlusRect) rather
    // than ever overlapping the header on a fence dragged shorter than this - MinEmptyStatePlusSize
    // is the floor that stops short of, not below.
    private const int EmptyStatePlusSize = 40;
    private const int MinEmptyStatePlusSize = 16;

    private const int CmdChangeFolder = 1;
    private const int CmdOpenInExplorer = 2;
    private const int CmdToggleHideLabels = 3;
    private const int CmdToggleOcdSizing = 4;
    private const int CmdResizeBoth = 5;
    private const int CmdResizeLeftRight = 6;
    private const int CmdResizeTopDown = 7;
    // Not a real WM_COMMAND id - just this row's own Row.Id value for the non-clickable "Fence
    // Dimensions" section header inside the OCD flyout, same as FenceForm's own
    // TagFenceDimensionsHeader.
    private const int TagFolderFenceDimensionsHeader = 1004;

    private readonly record struct GridEntry(string Path, bool IsDirectory);

    private const int BackButtonSize = 20;
    private const int BackButtonLeftGap = 4;
    private const int BackButtonTextGap = 4;

    // The folder tab's own width (see GetTabWidth/PaintFolderTab) - a fraction of contentWidth,
    // clamped to a sensible min/max.
    private const double TabWidthFraction = 0.42;
    private const int MinTabWidth = 60;
    private const int MaxTabWidth = 130;

    private readonly FolderFenceModel _model;
    private readonly FolderFenceManager _manager;
    // Only ever used to word the drag hint for an item over a different fence (see ComputeDragHint
    // below) - the same reference this widget's own base constructor already takes for snapping,
    // just also kept here since dragging a grid item out needs it too.
    private readonly FenceManager _fences;
    private readonly Dictionary<string, Icon?> _iconCache = new();
    private readonly List<GridEntry> _entries = new();
    private readonly Scrollbar _scrollbar = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 150 };

    // Relative path under _model.RootFolderPath currently being browsed - null means the root
    // itself. Persisted via _model.CurrentSubPath (see its own doc comment) - NavigateInto/NavigateUp
    // both write back to it, and the constructor seeds this field from it (once re-validated) rather
    // than always starting at the root the way this used to.
    private string? _currentSubPath;
    // Watches the root (recursively while browsing a subfolder) - see RestartWatcher.
    private FileSystemWatcher? _watcher;
    // Watches the root's parent - _watcher only ever sees the root's contents, never the root
    // itself, so without this a rename/move/delete of the root went completely unnoticed and the
    // grid kept showing stale entries pointing at paths that no longer existed.
    private FileSystemWatcher? _parentWatcher;
    // CurrentDirectory as of the last RestartWatcher - read by OnRootTreeChanged on a threadpool
    // thread to filter events before marshalling anything to the UI thread.
    private volatile string? _watchedDirectory;

    // RootFolderPath is set but that folder is gone (moved/deleted/drive unplugged) - shows the "+"
    // again with a "folder not found" note (see PaintEmptyState) so it can be re-pointed, rather than
    // an unexplained empty grid. Cached by RefreshEntries rather than hitting the disk every paint.
    private bool _rootMissing;

    private ContextMenuStrip? _itemContextMenu;
    private GridEntry? _contextEntry;

    private int _hoverIndex = -1;
    private bool _settingsButtonArmed;
    private bool _plusButtonArmed;
    private bool _backButtonArmed;

    // Drag of a grid item out of this fence - arms on mouse-down, then past DragThreshold hands the
    // item's real path to Windows' own DoDragDrop (see RunItemDrag), so it lands anywhere a file
    // from Explorer could: Explorer/the desktop/other apps, every fence's own OnDragDrop, and
    // sub-folder tiles in this or another folder fence. _draggingEntry is the entry itself rather
    // than an index - the watcher can refresh _entries mid-drag (a drop into one of this fence's
    // own sub-folders, say), and a stale index would then point at the wrong item.
    private const int DragThreshold = 4;
    private int? _dragArmIndex;
    private Point _dragArmPoint;
    private GridEntry? _draggingEntry;
    private DragGhostWindow? _dragGhost;

    public Guid FolderFenceId => _model.Id;

    /// <summary>Whether this folder fence is still in its empty "+" state - used by a folder fence's
    /// own item drag hint (see ComputeDragHint) to know a dragged subfolder landing here would
    /// connect this fence (see OnDragDrop) rather than be moved/copied into it.</summary>
    internal bool IsEmpty => NeedsFolder;

    /// <summary>Shows the "+" and accepts a folder to connect - either never pointed at one, or its
    /// folder has since gone missing (see _rootMissing).</summary>
    private bool NeedsFolder => _model.RootFolderPath is null || _rootMissing;

    /// <summary>Used only for the "Connect to {name}" drag hint (see ComputeDragHint) - every other
    /// cross-fence reference goes through FolderFenceId/FolderFenceManager instead. Mirrors
    /// FenceForm.FenceName.</summary>
    internal string FolderFenceName => _model.Name;

    /// <summary>The folder a file drop at screenPoint would land in - the sub-folder tile under the
    /// point if there is one, otherwise whichever folder is currently showing - plus the name the
    /// drag hint pill shows for it and that tile's index (-1 for the current folder itself, which
    /// has no tile to highlight). Null for an empty fence (a drop connects it instead - see
    /// OnDragDrop) or a folder that's since vanished.</summary>
    internal (string Path, string Name, int TileIndex)? DropTargetAt(Point screenPoint)
    {
        var dir = CurrentDirectory;
        if (dir is null || !Directory.Exists(dir))
            return null;

        if (IndexAtGridPosition(ToContent(PointToClient(screenPoint))) is int index && _entries[index].IsDirectory)
            return (_entries[index].Path, GetDisplayName(_entries[index]), index);
        return (dir, _currentSubPath is null ? _model.Name : Path.GetFileName(dir), -1);
    }

    /// <summary>Which model LayeredWidgetForm's own theme derivation and default Settings-dropdown
    /// rows read from - FolderFenceModel already implements IWidgetStyle.</summary>
    protected override IWidgetStyle Style => _model;

    protected override int OuterMargin => OuterMarginPx;
    // Unlike every other widget on this base, ButtonRowAtBottom's own DEFAULT (true) is the normal
    // state here, not the exceptional one - see ComputeButtonRowAtBottomFor below. TopBand reflects
    // that: the tab always gets at least TopMarginTabOnly (its own space, default state), growing
    // to TopMarginWithTab (tab + button row together) only once the button row itself flips up
    // there because the bottom band ran out of room.
    protected override int TopBand => ButtonRowAtBottom ? TopMarginTabOnly : TopMarginWithTab;
    protected override int BottomBand => ButtonRowAtBottom ? TopMargin : OuterMargin;
    protected override int MaxTopBand => TopMarginWithTab;

    /// <summary>A small bump over the base's plain 1px/ActiveBorderWidth - see BorderWidth's own doc
    /// comment on the base. GetBodyOutlinePath's diagonal tab cut is the only non-orthogonal segment
    /// in the whole outline, so at the base's stock width it visibly reads as thinner than the rest
    /// of the same single stroke.</summary>
    protected override float BorderWidth => base.BorderWidth + 0.6f;

    /// <summary>The button row's own default position is the BOTTOM band (see TopBand's own
    /// comment) - the base's usual "default top, flip to bottom near the top of the screen" trade
    /// doesn't fit here, since the tab always needs its own reserved space up top regardless of
    /// where the button row is. Flips to the top only when the bottom band itself wouldn't fit
    /// below body's own bottom edge - the mirror of what the base's own default check does for the
    /// top edge.</summary>
    protected override bool ComputeButtonRowAtBottomFor(Rectangle body)
    {
        var workArea = Screen.FromRectangle(body).WorkingArea;
        return body.Bottom + TopMargin <= workArea.Bottom;
    }

    /// <summary>Pushes Settings/Copy Settings/ExtraButtons ("−"/"×") up above the tab entirely,
    /// rather than into roughly the same content-Y band the tab itself occupies, whenever the
    /// button row is up there (either because it flipped there - see ComputeButtonRowAtBottomFor -
    /// or, on every other widget, because top is simply its default) - without this, whenever the
    /// Settings dropdown flips to the top-left (see ShouldSettingsButtonOpenLeft), the button row
    /// and the tab land on the same side and visually collide, since TopMarginWithTab's own extra
    /// room was sized to fit the button ABOVE the tab, not to give the tab a lane of its own next
    /// to it. Only while the tab could actually be showing at all (TitleVisible) - GetSettingsButtonRect
    /// itself already only ever applies this in the "button row is at the top" branch.</summary>
    protected override int SettingsButtonRowInset => TitleVisible ? TabExtraHeight : 0;

    // TitleVisible (== !HideHeader, the overridden property below - always true for this widget,
    // never the raw _model.HideHeader field) - HideHeader is forced off for a folder fence (see its
    // own override's doc comment on why), so reading _model.HideHeader directly here would disagree
    // with what's actually rendered whenever a fence's persisted HideHeader happens to still be true
    // (pre-existing data from before that override existed, say).
    private int GridTop => TitleVisible ? TitleBarHeight : 0;

    /// <summary>Item cell height when labels are hidden (FolderFenceModel.HideLabels) - just the
    /// icon plus a little breathing room, same as FenceForm's own EffectiveCellHeight.</summary>
    private int EffectiveCellHeight => _model.HideLabels ? IconTopPadding + IconSize + 8 : CellHeight;

    /// <summary>The empty state (RootFolderPath null - a fresh fence, or one just Cleared via the
    /// "−" button) shows a plain instruction instead of the fence's own name, since a never-pointed-
    /// at fence hasn't been given a meaningful name to show yet. Otherwise the header shows a
    /// breadcrumb ("base/sub/sub") while browsing into a subfolder, rather than just the fence's own
    /// bare name. Title itself (LayeredWidgetForm's own rename get/set hook) stays untouched either
    /// way, so renaming still only ever edits/seeds from the real base name, never this - a rename
    /// started from the empty state still opens with whatever _model.Name already is (usually still
    /// "Folder Fence"), not this instruction text. Forward slashes regardless of OS - reads naturally
    /// as a path breadcrumb either way, and this is display-only, never fed back into
    /// Path.Combine/GetRelativePath.</summary>
    protected override string DisplayTitle => _model.RootFolderPath is null
        ? "drag in or click + to connect folder"
        : _currentSubPath is null
            ? Title
            : $"{Title}/{_currentSubPath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/')}";

    /// <summary>Reserves room for the back button (see BackButtonRect) only while it's actually
    /// showing - at the root, the title text starts exactly where every other widget's does.</summary>
    protected override int TitleTextInset => _currentSubPath is null ? 0 : BackButtonSize + BackButtonTextGap;

    /// <summary>How wide the tab itself is for a given contentWidth - a fraction of it, clamped to
    /// a sensible min/max, and never wider than contentWidth itself on a very narrow fence. The
    /// floor is re-clamped down to that same ceiling (rather than passed to Math.Clamp as-is) for a
    /// fence narrower than MinTabWidth itself - Math.Clamp throws if its own min ends up greater
    /// than its max, which a plain MinTabWidth floor would do the moment contentWidth drops below
    /// it.</summary>
    private int GetTabWidth(int contentWidth)
    {
        var max = Math.Min(MaxTabWidth, contentWidth);
        var min = Math.Min(MinTabWidth, max);
        return Math.Clamp((int)(contentWidth * TabWidthFraction), min, max);
    }

    /// <summary>The whole widget's own outline, folder tab included - a single continuous path
    /// (tab's own rounded top-left corner and diagonal cut, straight into the header's own top
    /// edge, then an ordinary rounded body below) rather than two separately-stroked shapes (the
    /// tab's own border plus the body's own) that happen to sit end-to-end. Two independently-
    /// antialiased strokes sharing the same boundary - even a mathematically exact one - leave a
    /// faint seam where each one's own partial-coverage edge pixels don't quite agree, the same
    /// "double antialiasing" issue PaintChrome's own body/title fill order used to have (see
    /// RoundedRectPath.BottomFilled's own doc comment) - one path stroked once is the only way to
    /// avoid it. GetHeaderFillPath fills the tab (and header) now; this is what actually borders it. Falls back
    /// to the base's plain rounded rectangle only when the tab itself never shows at all
    /// (HideHeader) - unlike most of this widget's other tab-aware hooks, this one does NOT also
    /// check ButtonRowAtBottom: TopBand always reserves at least TopMarginTabOnly for the tab
    /// regardless of that flag now (see its own comment), so the tab is showing either way.</summary>
    protected override GraphicsPath GetBodyOutlinePath(int contentWidth, int contentHeight, int cornerRadius)
    {
        if (!TitleVisible)
            return base.GetBodyOutlinePath(contentWidth, contentHeight, cornerRadius);

        var tabWidth = GetTabWidth(contentWidth);
        var diagonalRun = Math.Min(TabExtraHeight, tabWidth);
        var tabRadius = Math.Max(0, Math.Min(cornerRadius, Math.Min(TabExtraHeight, tabWidth - diagonalRun) / 2));
        var td = tabRadius * 2;
        var bd = cornerRadius * 2;

        var body = ToWindow(new Rectangle(0, 0, contentWidth - 1, contentHeight - 1));
        var tabTopY = ToWindow(new Point(0, -TabExtraHeight)).Y;

        var path = new GraphicsPath();
        if (tabRadius > 0)
            path.AddArc(body.X, tabTopY, td, td, 180, 90);
        path.AddLine(body.X + tabRadius, tabTopY, body.X + tabWidth - diagonalRun, tabTopY);
        path.AddLine(body.X + tabWidth - diagonalRun, tabTopY, body.X + tabWidth, body.Y);
        path.AddLine(body.X + tabWidth, body.Y, body.Right - cornerRadius, body.Y);
        if (cornerRadius > 0)
            path.AddArc(body.Right - bd, body.Y, bd, bd, 270, 90);
        path.AddLine(body.Right, body.Y + cornerRadius, body.Right, body.Bottom - cornerRadius);
        if (cornerRadius > 0)
            path.AddArc(body.Right - bd, body.Bottom - bd, bd, bd, 0, 90);
        path.AddLine(body.Right - cornerRadius, body.Bottom, body.X + cornerRadius, body.Bottom);
        if (cornerRadius > 0)
            path.AddArc(body.X, body.Bottom - bd, bd, bd, 90, 90);
        path.AddLine(body.X, body.Bottom - cornerRadius, body.X, tabTopY + tabRadius);
        path.CloseFigure();
        return path;
    }

    /// <summary>The header's own fill shape, PLUS the tab's - square top-left corner on the header
    /// part instead of the base's usual rounded one (see RoundedRectPath.TopSquareTopLeft's own doc
    /// comment for why: the tab sits entirely above the header, never overlapping down into it, so a
    /// rounded cutout here has nothing else covering it), with the tab's own rounded-top-left/
    /// diagonal-cut shape appended as a second closed figure on the same path (StartFigure - the
    /// header shape above already ends in its own CloseFigure). Both figures fill in the same
    /// ThemedTitle color and never overlap, so combining them into one path/one FillPath call (see
    /// PaintChrome's own header-fill step) is exactly equivalent to painting them separately - this
    /// used to be two independent calls (this method for the header, a since-removed PaintFolderTab
    /// for the tab), which was harmless for the fill itself (no seam risk between two same-colored
    /// same-opacity fills - unlike GetBodyOutlinePath's border stroke, where two independently-
    /// antialiased strokes DO disagree at their shared boundary) but meant PaintContent needed its
    /// own extra "if (TitleVisible) PaintFolderTab(...)" call that this removes.
    ///
    /// The tab geometry here must stay in lockstep with GetBodyOutlinePath's own tab segments (same
    /// tabWidth/diagonalRun/tabRadius math) - they trace the same edge, one as a fill boundary and
    /// one as a border stroke centered (well, inset) on it. Only ever called while TitleVisible is
    /// already true (see PaintChrome), and (see GetBodyOutlinePath's own comment) the tab is showing
    /// regardless of ButtonRowAtBottom now, so this never needs to fall back to the base's own
    /// rounded corner.</summary>
    protected override GraphicsPath GetHeaderFillPath(int contentWidth, int cornerRadius)
    {
        var path = RoundedRectPath.TopSquareTopLeft(ToWindow(new Rectangle(0, 0, contentWidth - 1, TitleRowHeight)), cornerRadius);

        var tabWidth = GetTabWidth(contentWidth);
        var bounds = ToWindow(new Rectangle(0, -TabExtraHeight, tabWidth, TabExtraHeight));
        var diagonalRun = Math.Min(TabExtraHeight, bounds.Width);
        var tabRadius = Math.Max(0, Math.Min(cornerRadius, Math.Min(bounds.Height, bounds.Width - diagonalRun) / 2));
        var d = tabRadius * 2;

        path.StartFigure();
        if (tabRadius > 0)
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddLine(bounds.X + tabRadius, bounds.Y, bounds.Right - diagonalRun, bounds.Y);
        path.AddLine(bounds.Right - diagonalRun, bounds.Y, bounds.Right, bounds.Bottom);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
        path.CloseFigure();

        return path;
    }

    /// <summary>The "<-" glyph's own rect, content-relative - sits inside the header row itself, to
    /// the left of the (possibly breadcrumbed) title text. Only where the glyph is drawn - the
    /// clickable "go back" area is the whole header row (see HeaderClickableRect), not just this.</summary>
    private Rectangle BackButtonRect() =>
        new(BackButtonLeftGap, (TitleBarHeight - BackButtonSize) / 2, BackButtonSize, BackButtonSize);

    /// <summary>The entire header row, content-relative - while browsing a subfolder
    /// (_currentSubPath set), a plain left click anywhere on it goes back up a level, not just on
    /// the small "<-" glyph itself (see BackButtonRect) - reads as "the whole header is the back
    /// button" while there's somewhere to go back to. Only meaningful while the header is actually
    /// showing at all (TitleVisible) - callers already guard on that too.</summary>
    private Rectangle HeaderClickableRect(int contentWidth) => new(0, 0, contentWidth, TitleBarHeight);

    /// <summary>Copy Folder Fence duplicates this fence's own settings into a new, empty folder
    /// fence (see FolderFenceManager.CreateFolderFenceLike) - same two-squares "duplicate" glyph
    /// FenceForm's own Copy Fence button uses (see LayeredWidgetForm.PaintCopyIconGlyph), for the
    /// same action on the same kind of widget. "−" clears RootFolderPath back to the empty "+" state
    /// (see ClearFolder); "×" deletes this widget entirely, with confirmation (see ConfirmDelete) -
    /// plain ChromeButtons, since neither a minus sign nor an "x" needs a custom glyph of its own.
    /// Declared in this order (Copy closest to Copy Settings, Delete outermost) so on a narrowing
    /// fence Delete drops off the bar into the Settings dropdown first, matching FenceForm's own
    /// Copy/Delete ordering.</summary>
    protected override IReadOnlyList<ChromeButton> ExtraButtons { get; }

    public FolderFenceForm(FolderFenceModel model, FolderFenceManager manager, FenceManager fences)
        : base(model.Opacity / 100f, fences)
    {
        _model = model;
        _manager = manager;
        _fences = fences;

        // Restores whichever subfolder this fence was last browsed into, re-validated against disk
        // first - the real folder could have been renamed/deleted/moved since the last session, in
        // which case this falls back to the root (and corrects the stale value on this same Save
        // pass) rather than trusting it blindly.
        if (_model.RootFolderPath is not null && _model.CurrentSubPath is not null)
        {
            if (Directory.Exists(Path.Combine(_model.RootFolderPath, _model.CurrentSubPath)))
                _currentSubPath = _model.CurrentSubPath;
            else
                _model.CurrentSubPath = null;
        }

        ExtraButtons = new List<ChromeButton>
        {
            new("+", 22, () => _manager.CreateFolderFenceLike(FolderFenceId), "Copy Folder Fence", PaintCopyIconGlyph),
            new("−", 22, ClearFolder, "Clear Folder"),
            new("×", 22, ConfirmDelete, "Delete Folder Fence"),
        };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;
        // Same reasoning as FenceForm's own Font assignment - every base rename/hit-test/paint path
        // measures against Control.Font, not the WinForms default.
        Font = AppTheme.Font;

        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Resync();
        };

        RefreshEntries();
        // Brings a fence saved with a name of its own (from before names were synced - see Title),
        // or whose folder was renamed while the app wasn't running, back in line with its folder.
        if (_model.RootFolderPath is not null && !_rootMissing)
            _model.Name = FolderDisplayName(_model.RootFolderPath);
        RestartWatcher();
        RenderAndPresent();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;

            // Control's base constructor probes CreateParams before our own constructor body has
            // run (so _model is still null at that point) - same as FenceForm/WidgetManagerWidget.
            if (_model is null)
                return cp;

            ButtonRowAtBottom = ComputeButtonRowAtBottomFor(_model.Bounds);

            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN;
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
            cp.X = _model.Bounds.X - OuterMargin;
            cp.Y = _model.Bounds.Y - TopBand;
            cp.Width = _model.Bounds.Width + OuterMargin * 2;
            cp.Height = _model.Bounds.Height + TopBand + BottomBand;
            return cp;
        }
    }

    public new void Show() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);

    public void SetVisible(bool visible) =>
        NativeMethods.ShowWindow(Handle, visible ? NativeMethods.SW_SHOWNOACTIVATE : NativeMethods.SW_HIDE);

    protected override Rectangle GetCurrentBody() => _model.Bounds;
    protected override int SnapMargin => _model.Margin;

    /// <summary>Extends the plain body's own top edge up by TabExtraHeight whenever the folder tab
    /// could be showing at all (see InflateForTabSnap) - a different widget dragged toward this one
    /// snaps flush against the tab's own visible top edge, not the invisible gap above it that a
    /// plain GetCurrentBody() would otherwise offer. This widget's own drag/resize math is untouched
    /// (see GetSnapTargetBody's own doc comment on why it's a separate hook).</summary>
    protected override Rectangle GetSnapTargetBody() => InflateForTabSnap(GetCurrentBody());

    // BeginSnapDrag/SupportsResize/ResizableEdges all use LayeredWidgetForm's own defaults
    // unchanged - full resize, snapping against every other live widget's edges and custom snap
    // lines, same as any other widget on this base. ComputeMovedBody/ComputeResizedBody below are
    // the exception - see their own doc comments.

    /// <summary>Same tab-aware treatment as GetSnapTargetBody, but for the other direction: THIS
    /// widget being the one dragged/resized, snapping its own top edge against everyone else's.
    /// base.ComputeMovedBody/ComputeResizedBody only ever compare whatever rect they're handed
    /// against other widgets' edges - they don't care that the rect's own top here has been pushed
    /// up by TabExtraHeight first, so this fence's own tab (not the invisible gap below it) is
    /// what ends up flush against another widget's edge. Deflated back down by the same amount
    /// afterward, so the actual returned/persisted body still means "the plain body", matching
    /// what GetCurrentBody/OnDragEnd/CreateParams all expect.</summary>
    protected override Rectangle ComputeMovedBody(Rectangle proposedBody) =>
        DeflateForTabSnap(base.ComputeMovedBody(InflateForTabSnap(proposedBody)));

    /// <summary>Same idea as ComputeMovedBody above, but only inflated/deflated while the resize's
    /// own active edges actually include Top - a resize dragging only the bottom or a side edge
    /// never moves the top at all, so there's nothing there to snap differently in the first
    /// place.</summary>
    protected override Rectangle ComputeResizedBody(Rectangle proposedBody, SnapEdges activeEdges)
    {
        if (!activeEdges.HasFlag(SnapEdges.Top))
            return base.ComputeResizedBody(proposedBody, activeEdges);
        return DeflateForTabSnap(base.ComputeResizedBody(InflateForTabSnap(proposedBody), activeEdges));
    }

    // Neither checks ButtonRowAtBottom (unlike SettingsButtonRowInset, which only matters while the
    // button row is actually up in the tab's own space) - the tab itself shows regardless of that
    // flag now (see TopBand/GetBodyOutlinePath's own comments), so it's always the right edge to
    // snap against whenever there's a header at all.
    private Rectangle InflateForTabSnap(Rectangle body) =>
        !TitleVisible ? body : new Rectangle(body.X, body.Y - TabExtraHeight, body.Width, body.Height + TabExtraHeight);

    private Rectangle DeflateForTabSnap(Rectangle inflated) =>
        !TitleVisible ? inflated : new Rectangle(inflated.X, inflated.Y + TabExtraHeight, inflated.Width, inflated.Height - TabExtraHeight);

    protected override void OnDragEnd()
    {
        if (NativeMethods.GetWindowRect(Handle, out var rect))
        {
            _model.Bounds = Rectangle.FromLTRB(
                rect.Left + OuterMargin, rect.Top + TopBand, rect.Right - OuterMargin, rect.Bottom - BottomBand);
            _manager.Save();
        }

        // OCD Fence Sizing: snap to the tightest fit right after a manual resize, on top of
        // whatever size was just dragged to - not after a move, same as FenceForm's own OnDragEnd.
        if (IsResizing && _model.OcdFenceSizing)
            FormatDimensions(adjustWidth: true, adjustHeight: true);
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

        // Same "carve this rect out of the header's own non-client HTBORDER territory" treatment as
        // IsOverHeaderCloseButton above - not gated by ShowsButtons either, since this is core
        // navigation, not extra engagement-only chrome. The whole header row while browsing a
        // subfolder, not just the "<-" glyph itself - see HeaderClickableRect.
        if (_currentSubPath is not null && TitleVisible && HeaderClickableRect(contentWidth).Contains(contentPoint))
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

        if (TitleVisible && y - TopBand <= TitleBarHeight)
            return HTBORDER;

        return HTCLIENT;
    }

    protected override void OnDragEnter(DragEventArgs e) => UpdateDropEffect(e);

    // Re-evaluated on every move, not just on enter - the target (and so the effect) changes as the
    // cursor crosses sub-folder tiles, and Ctrl/Shift can flip move/copy mid-drag.
    protected override void OnDragOver(DragEventArgs e) => UpdateDropEffect(e);

    protected override void OnDragLeave(EventArgs e) => SetHoverIndex(-1);

    private void UpdateDropEffect(DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return;

        if (NeedsFolder)
        {
            if (paths.Length == 1 && Directory.Exists(paths[0]))
                e.Effect = DragDropEffects.Link;
            return;
        }

        // Highlights the sub-folder tile a drop would land in, the same cue Explorer gives.
        var target = DropTargetAt(new Point(e.X, e.Y));
        if (target is { } t)
            e.Effect = TransferEffect(paths, t.Path, e);
        SetHoverIndex(e.Effect != DragDropEffects.None && target is { TileIndex: >= 0 } hit ? hit.TileIndex : -1);
    }

    /// <summary>Explorer's own rule for a file drop into targetDir: Ctrl copies, Shift moves,
    /// otherwise a move within one drive and a copy across drives - falling back to whichever of the
    /// two the source allows. None when there's nothing to do: a folder into itself or its own
    /// subtree, or every path already directly inside targetDir.</summary>
    private static DragDropEffects TransferEffect(string[] paths, string targetDir, DragEventArgs e)
    {
        const int ShiftKey = 4;
        const int CtrlKey = 8;

        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir));
        var anythingToTransfer = false;
        foreach (var path in paths)
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return DragDropEffects.None;
            if (!string.Equals(Path.GetDirectoryName(source), target, StringComparison.OrdinalIgnoreCase))
                anythingToTransfer = true;
        }
        if (!anythingToTransfer)
            return DragDropEffects.None;

        var sameDrive = paths.All(p => string.Equals(Path.GetPathRoot(Path.GetFullPath(p)), Path.GetPathRoot(target),
            StringComparison.OrdinalIgnoreCase));
        var preferred = (e.KeyState & CtrlKey) != 0 ? DragDropEffects.Copy
            : (e.KeyState & ShiftKey) != 0 ? DragDropEffects.Move
            : sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffect & preferred) != 0)
            return preferred;
        var fallback = preferred == DragDropEffects.Move ? DragDropEffects.Copy : DragDropEffects.Move;
        return (e.AllowedEffect & fallback) != 0 ? fallback : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        SetHoverIndex(-1);
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return;

        // An empty fence (or one whose folder has gone missing) adopts a single dropped folder as
        // its root.
        if (NeedsFolder)
        {
            if (paths.Length == 1 && Directory.Exists(paths[0]))
                SetRootFolder(paths[0]);
            return;
        }

        // A populated fence mirrors a real folder, so a drop moves or copies the files for real -
        // into the sub-folder tile it landed on, or else whichever folder is currently showing,
        // like Explorer. The watcher then picks up the change and refreshes the grid.
        if (DropTargetAt(new Point(e.X, e.Y)) is not { } target)
            return;
        var effect = TransferEffect(paths, target.Path, e);
        if (effect != DragDropEffects.None)
            FileTransferOperations.TransferInto(Handle, paths, target.Path, move: effect == DragDropEffects.Move);
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
        if (_currentSubPath is not null && TitleVisible && HeaderClickableRect(contentSize.Width).Contains(contentPoint))
        {
            _backButtonArmed = true;
            return;
        }
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

        if (NeedsFolder && EmptyStatePlusRect(contentSize).Contains(contentPoint))
        {
            _plusButtonArmed = true;
            return;
        }

        if (_model.RootFolderPath is not null && IndexAtGridPosition(contentPoint) is int index)
        {
            _dragArmIndex = index;
            _dragArmPoint = e.Location; // raw window-space is fine here - only ever used as a delta
        }
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

        if (_draggingEntry is null && _dragArmIndex is int armIndex && MouseButtons == MouseButtons.Left)
        {
            var dx = e.X - _dragArmPoint.X;
            var dy = e.Y - _dragArmPoint.Y;
            if (dx * dx + dy * dy >= DragThreshold * DragThreshold)
            {
                _dragArmIndex = null;
                RunItemDrag(_entries[armIndex]);
                return;
            }
        }

        SetHoverIndex(_model.RootFolderPath is null ? -1 : IndexAtGridPosition(ToContent(e.Location)) ?? -1);
        // Clear Folder/Delete Folder Fence's own hover tooltip is LayeredWidgetForm's own now (see
        // ExtraButtons) - base.OnMouseMove above already ran UpdateButtonHover.
    }

    /// <summary>Hands entry to Windows' own drag-and-drop loop so it can land anywhere a real file
    /// can - Explorer, the desktop, other apps, and every fence's own OnDragDrop (which already
    /// covers add-to-fence, Recycle Bin, convert and connect) - while keeping this app's own ghost
    /// card + hint pill: DoDragDrop's modal loop swallows this form's mouse messages until the drop,
    /// so OnGiveFeedback moves the ghost instead. Blocks until the drop lands or Esc cancels it. The
    /// source never deletes anything itself afterwards - a move is carried out by whichever target
    /// accepted it, and the watcher refreshes this grid.</summary>
    private void RunItemDrag(GridEntry entry)
    {
        _draggingEntry = entry;
        _dragGhost = new DragGhostWindow(GetIcon(entry.Path), GetDisplayName(entry));
        _dragGhost.MoveTo(Cursor.Position);
        SetHoverIndex(-1);
        RenderAndPresent();
        try
        {
            // Link is offered alongside Copy/Move because an empty fence or folder fence answers a
            // single-folder drop with Link (convert/connect - see their own OnDragEnter), which OLE
            // would otherwise refuse outright.
            DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { entry.Path }),
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _draggingEntry = null;
            _dragGhost?.Dispose();
            _dragGhost = null;
            RenderAndPresent();
        }
    }

    // Called by DoDragDrop after every DragOver the current target answers (or on every move with
    // no target at all), so it tracks the cursor as closely as OnMouseMove did before. The default
    // OS cursor stays, carrying its own move/copy/no-drop badge beside the ghost.
    protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
    {
        base.OnGiveFeedback(e);
        if (_dragGhost is null)
            return;
        var screenPoint = Cursor.Position;
        _dragGhost.SetHint(ComputeDragHint(screenPoint, e.Effect));
        _dragGhost.MoveTo(screenPoint);
    }

    /// <summary>Live drop-target hint for a grid item drag (see RunItemDrag), shown in the pill below
    /// the drag ghost. effect is what the target under the cursor said it would do - None hides the
    /// pill. Our own fences get specific wording that mirrors FenceForm's own ComputeDragHint; any
    /// other window (Explorer, the desktop, another app) only gets the generic effect, since that's
    /// all it tells us.</summary>
    private string? ComputeDragHint(Point screenPoint, DragDropEffects effect)
    {
        if (_draggingEntry is not { } entry || effect == DragDropEffects.None)
            return null;

        if (_fences.FindFenceAt(screenPoint, _model.Id) is { } targetForm)
        {
            // Same rule FenceForm.OnDragDrop applies - a subfolder dropped on a currently empty
            // fence converts it instead of adding an ordinary shortcut.
            if (entry.IsDirectory && targetForm.IsEmpty)
                return "Convert to Folder Fence";

            var targetIndex = targetForm.IndexForExternalDrop(screenPoint);
            return _fences.IsRecycleBinAt(targetForm.FenceId, targetIndex)
                ? "Move to Recycle Bin"
                : $"Add to {targetForm.FenceName}";
        }

        var verb = (effect & DragDropEffects.Move) != 0 ? "Move"
            : (effect & DragDropEffects.Copy) != 0 ? "Copy"
            : null;

        // Any folder fence, this one included - dropping onto one of this fence's own sub-folder
        // tiles moves the item there.
        if (_manager.FindFolderFenceAt(screenPoint, Guid.Empty) is { } folderFence)
        {
            if (folderFence.IsEmpty)
                return $"Connect to {folderFence.FolderFenceName}";
            return verb is not null && folderFence.DropTargetAt(screenPoint) is { } target
                ? $"{verb} to {target.Name}"
                : null;
        }

        return verb is not null ? $"{verb} here"
            : (effect & DragDropEffects.Link) != 0 ? "Create shortcut here"
            : null;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentSize = GetContentSize();
        var onLeft = ShouldSettingsButtonOpenLeft(contentSize.Width);

        FireArmedHeaderCloseButton(contentPoint);

        if (_backButtonArmed)
        {
            _backButtonArmed = false;
            if (_currentSubPath is not null && TitleVisible && HeaderClickableRect(contentSize.Width).Contains(contentPoint))
                NavigateUp();
            return;
        }

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            if (ShowsButtons && GetSettingsButtonRect(contentSize.Width, onLeft).Contains(contentPoint))
                OpenSettingsMenu();
            return;
        }

        FireArmedExtraButton(contentPoint);

        if (_scrollbar.EndDrag())
        {
            Capture = false;
            return;
        }

        if (_plusButtonArmed)
        {
            _plusButtonArmed = false;
            if (EmptyStatePlusRect(contentSize).Contains(contentPoint))
                BrowseForFolder();
            return;
        }

        // A completed item drag never gets here - DoDragDrop consumes its mouse-up (see RunItemDrag).
        _dragArmIndex = null;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_model.RootFolderPath is null)
            return;
        if (IndexAtGridPosition(ToContent(e.Location)) is not int index)
            return;

        var entry = _entries[index];
        if (entry.IsDirectory)
            NavigateInto(entry.Path);
        else
            OpenItem(entry.Path);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var contentSize = GetContentSize();
        var maxScroll = GetMaxScroll(contentSize.Width, contentSize.Height);
        if (_scrollbar.HandleWheel(e.Delta, EffectiveCellHeight, maxScroll))
            RenderAndPresent();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoverIndex(-1);
    }

    protected override void OnClientRightClick(Point contentPoint)
    {
        if (_model.RootFolderPath is null)
            return;
        if (IndexAtGridPosition(contentPoint) is not int index)
            return;
        _contextEntry = _entries[index];
        ShowItemContextMenu();
    }

    private void SetHoverIndex(int index)
    {
        if (index == _hoverIndex)
            return;
        _hoverIndex = index;
        RenderAndPresent();
    }

    /// <summary>Square, centered in the space below the header/tab - shrinks below its own default
    /// EmptyStatePlusSize (rather than the old fixed size regardless of available room) once a fence
    /// dragged short enough no longer has that much vertical space to give it, the same re-clamped-
    /// floor-to-ceiling approach GetTabWidth uses for the same reason: without this, the glyph used
    /// to keep its full fixed size and get pushed up out of its own area into the header - Math.Min
    /// against contentSize.Width too, so an equally narrow (rather than short) fence can't push it
    /// past the left/right edges either. Centered within the same GridTop+GridPadding-to-
    /// contentHeight-GridPadding area the real item grid itself occupies (see GetMaxScroll/
    /// FormatDimensions' own identical top/bottom padding), not the bare unpadded region below the
    /// header - besides matching the real grid's own margins, this also keeps a real gap above the
    /// glyph at any size, rather than it ever sitting flush against the header's own bottom edge.</summary>
    private Rectangle EmptyStatePlusRect(Size contentSize)
    {
        var top = GridTop + GridPadding;
        var areaHeight = Math.Max(0, contentSize.Height - top - GridPadding);
        var maxSize = Math.Max(0, Math.Min(areaHeight, contentSize.Width));
        var size = Math.Clamp(EmptyStatePlusSize, Math.Min(MinEmptyStatePlusSize, maxSize), maxSize);
        return new Rectangle(
            (contentSize.Width - size) / 2, top + (areaHeight - size) / 2,
            size, size);
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder for this fence to mirror" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SetRootFolder(dialog.SelectedPath);
    }

    /// <summary>Points this fence at path and renames it after that folder - the fence name always
    /// mirrors its folder's name (see Title).</summary>
    private void SetRootFolder(string path)
    {
        _model.RootFolderPath = path;
        _currentSubPath = null;
        _model.CurrentSubPath = null;
        _model.Name = FolderDisplayName(path);
        _manager.Save();
        RefreshEntries();
        RestartWatcher();
        RenderAndPresent();
    }

    /// <summary>The folder's own name, or the whole path for a drive root ("D:\"), which has none.</summary>
    private static string FolderDisplayName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void ClearFolder()
    {
        if (_model.RootFolderPath is null)
            return;
        _model.RootFolderPath = null;
        _currentSubPath = null;
        _model.CurrentSubPath = null;
        _rootMissing = false;
        DisposeWatchers();
        _entries.Clear();
        _hoverIndex = -1;
        _manager.Save();
        RenderAndPresent();
    }

    private void ConfirmDelete()
    {
        var result = MessageBox.Show(this,
            $"Delete folder fence \"{_model.Name}\"? This only removes the widget - nothing on disk is deleted.",
            "Delete Folder Fence", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result == DialogResult.Yes)
            _manager.DeleteFolderFence(_model.Id);
    }

    protected override void OnHeaderCloseButtonClick() => ConfirmDelete();

    private void NavigateInto(string path)
    {
        _currentSubPath = Path.GetRelativePath(_model.RootFolderPath!, path);
        _model.CurrentSubPath = _currentSubPath;
        _manager.Save();
        _scrollbar.Offset = 0;
        RefreshEntries();
        RestartWatcher();
        RenderAndPresent();
    }

    private void NavigateUp()
    {
        if (_currentSubPath is null)
            return;
        var parent = Path.GetDirectoryName(_currentSubPath);
        _currentSubPath = string.IsNullOrEmpty(parent) ? null : parent;
        _model.CurrentSubPath = _currentSubPath;
        _manager.Save();
        _scrollbar.Offset = 0;
        RefreshEntries();
        RestartWatcher();
        RenderAndPresent();
    }

    private string? CurrentDirectory =>
        _model.RootFolderPath is null ? null :
        _currentSubPath is null ? _model.RootFolderPath : Path.Combine(_model.RootFolderPath, _currentSubPath);

    /// <summary>Rescans CurrentDirectory fresh off disk - folders first, then files, alphabetical
    /// within each (Explorer's own default ordering). Never throws: a folder that's become
    /// inaccessible or vanished out from under this fence just shows as empty rather than crashing
    /// it, the same tolerant approach GetIcon below already takes for a single missing file. Also
    /// re-checks the root itself (see _rootMissing), and drops back to the root when only the
    /// subfolder being browsed has gone.</summary>
    private void RefreshEntries()
    {
        _entries.Clear();
        _hoverIndex = -1;

        _rootMissing = _model.RootFolderPath is not null && !Directory.Exists(_model.RootFolderPath);
        if (!_rootMissing && _currentSubPath is not null && !Directory.Exists(CurrentDirectory))
        {
            _currentSubPath = null;
            _model.CurrentSubPath = null;
            _scrollbar.Offset = 0;
            _manager.Save();
        }

        var dir = CurrentDirectory;
        if (dir is null || !Directory.Exists(dir))
            return;

        try
        {
            foreach (var d in Directory.GetDirectories(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                _entries.Add(new GridEntry(d, IsDirectory: true));
            foreach (var f in Directory.GetFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                _entries.Add(new GridEntry(f, IsDirectory: false));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }

    /// <summary>(Re)builds the watchers for whatever's on screen - called whenever that changes
    /// (SetRootFolder, NavigateInto/Up, and Resync when the folder vanishes or comes back). A folder
    /// that can't be watched (permissions, or it vanished) just doesn't live-refresh; RefreshEntries'
    /// own try/catch above already keeps the grid itself from crashing on the same problem.
    ///
    /// Never holds anything open INSIDE the root: Windows refuses to rename a folder while anything
    /// under it is open, so a watcher on the subfolder being browsed would stop the root being renamed
    /// in Explorer. Instead the root itself is watched - recursively while browsing a subfolder, with
    /// OnRootTreeChanged filtering down to the level on screen and the folders leading to it - plus a
    /// non-recursive watcher on the root's parent (see _parentWatcher) to notice the root itself
    /// being renamed/moved/deleted, which no watcher on the root can see.</summary>
    private void RestartWatcher()
    {
        DisposeWatchers();
        _watchersStale = false;
        _watchedDirectory = CurrentDirectory;

        if (_model.RootFolderPath is null)
            return;
        var root = Path.TrimEndingDirectorySeparator(_model.RootFolderPath);

        if (!_rootMissing)
        {
            _watcher = TryCreateWatcher(root, NotifyFilters.FileName | NotifyFilters.DirectoryName,
                recursive: _currentSubPath is not null, OnRootTreeChanged);
            if (Path.GetDirectoryName(root) is { } parent)
                _parentWatcher = TryCreateWatcher(parent, NotifyFilters.DirectoryName, recursive: false, OnAncestorChanged);
        }
        else
        {
            // Just the nearest ancestor that still exists, so the folder coming back (moved back,
            // drive plugged in again) is noticed.
            var existing = Path.GetDirectoryName(root);
            while (existing is not null && !Directory.Exists(existing))
                existing = Path.GetDirectoryName(existing);
            if (existing is not null)
                _parentWatcher = TryCreateWatcher(existing, NotifyFilters.DirectoryName, recursive: false, OnAncestorChanged);
        }
    }

    private FileSystemWatcher? TryCreateWatcher(string dir, NotifyFilters filter, bool recursive, FileSystemEventHandler handler)
    {
        try
        {
            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = filter,
                // A recursive watch over a busy tree (a build under the root, say) sees far more
                // events than the level on screen - the max buffer makes an overflow (which still
                // recovers via OnWatcherError's full rescan) much rarer.
                InternalBufferSize = recursive ? 64 * 1024 : 8 * 1024,
            };
            watcher.Created += handler;
            watcher.Deleted += handler;
            watcher.Renamed += (s, e) => handler(s, e);
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private void DisposeWatchers()
    {
        _watcher?.Dispose();
        _watcher = null;
        _parentWatcher?.Dispose();
        _parentWatcher = null;
    }

    /// <summary>Anything under the root changed - runs on a threadpool thread, and for a recursive
    /// watch over a busy tree most events are irrelevant, so they're filtered here before anything
    /// is marshalled to the UI thread. A change directly inside the folder on screen refreshes the
    /// grid; a change to one of the folders leading down to it is handled like an ancestor change
    /// (see OnAncestorChanged); everything else is ignored.</summary>
    private void OnRootTreeChanged(object sender, FileSystemEventArgs e)
    {
        if (_watchedDirectory is not { } current)
            return;

        var oldPath = (e as RenamedEventArgs)?.OldFullPath;
        if (IsDirectlyInside(e.FullPath, current) || (oldPath is not null && IsDirectlyInside(oldPath, current)))
            PostToUi(ScheduleRefresh);
        else if (IsSameOrUnder(current, e.FullPath) || (oldPath is not null && IsSameOrUnder(current, oldPath)))
            OnAncestorChanged(sender, e);
    }

    private static bool IsDirectlyInside(string path, string dir) =>
        string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase);

    // Set when a watcher errored (buffer overflow, or its folder was deleted under it) or an ancestor
    // changed, so the next Resync rebuilds the watchers even if CurrentDirectory itself didn't change.
    private bool _watchersStale;

    /// <summary>_refreshTimer's own tick - rescans, then rebuilds the watchers only if what they
    /// should be watching changed (a subfolder vanished and RefreshEntries fell back to the root, the
    /// root went missing or came back, or _watchersStale).</summary>
    private void Resync()
    {
        var before = CurrentDirectory;
        var wasMissing = _rootMissing;
        RefreshEntries();
        if (_watchersStale || wasMissing != _rootMissing
            || !string.Equals(before, CurrentDirectory, StringComparison.OrdinalIgnoreCase))
            RestartWatcher();
        RenderAndPresent();
    }

    /// <summary>Debounced through _refreshTimer so a burst of filesystem activity (copying many files
    /// at once, say) coalesces into one refresh instead of flooding RenderAndPresent.</summary>
    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e) =>
        PostToUi(() =>
        {
            _watchersStale = true;
            ScheduleRefresh();
        });

    /// <summary>A folder above the one on screen changed. A rename of the root or of a subfolder on
    /// the way down to CurrentDirectory is followed in place (see FollowRename); anything else
    /// touching that path (deleted, moved elsewhere, or coming back) just triggers a Resync, which
    /// then shows the missing-folder state or falls back to the root as needed. Changes to unrelated
    /// siblings are ignored.</summary>
    private void OnAncestorChanged(object sender, FileSystemEventArgs e) =>
        PostToUi(() =>
        {
            if (CurrentDirectory is not { } current)
                return;
            if (e is RenamedEventArgs renamed && !_rootMissing && IsSameOrUnder(current, renamed.OldFullPath))
            {
                FollowRename(renamed.OldFullPath, renamed.FullPath);
                return;
            }
            if (IsSameOrUnder(current, e.FullPath))
            {
                _watchersStale = true;
                ScheduleRefresh();
            }
        });

    /// <summary>oldPath (the root, or a subfolder between it and CurrentDirectory) was renamed to
    /// newPath within the same parent - repoints the fence at the new location rather than treating
    /// it as gone. A rename of the root renames the fence to match (see Title).</summary>
    private void FollowRename(string oldPath, string newPath)
    {
        var root = _model.RootFolderPath!;
        if (IsSameOrUnder(root, oldPath))
        {
            _model.RootFolderPath = Rebase(root, oldPath, newPath);
            _model.Name = FolderDisplayName(_model.RootFolderPath);
        }
        else
            _currentSubPath = Path.GetRelativePath(root, Rebase(CurrentDirectory!, oldPath, newPath));
        _model.CurrentSubPath = _currentSubPath;
        _manager.Save();

        RefreshEntries();
        RestartWatcher();
        RenderAndPresent();
    }

    private static string Rebase(string path, string oldPrefix, string newPrefix)
    {
        var relative = Path.GetRelativePath(oldPrefix, path);
        return relative == "." ? newPrefix : Path.Combine(newPrefix, relative);
    }

    /// <summary>Whether path is ancestor itself or somewhere inside it.</summary>
    private static bool IsSameOrUnder(string path, string ancestor)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        var a = Path.TrimEndingDirectorySeparator(ancestor);
        return string.Equals(p, a, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Watcher events fire on a threadpool thread, not the UI thread.</summary>
    private void PostToUi(Action action)
    {
        if (IsDisposing)
            return;
        try
        {
            BeginInvoke(action);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void OpenItem(string? path)
    {
        if (path is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // The file may have been moved/deleted since it was last scanned.
        }
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
        }
    }

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
        menu.Items.Add("Open", null, (_, _) => OpenContextEntry());
        menu.Items.Add("Show in Explorer", null, (_, _) => { if (_contextEntry is { } entry) ShowInExplorer(entry.Path); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete", null, (_, _) => ConfirmDeleteContextEntry());
        return menu;
    }

    private void OpenContextEntry()
    {
        if (_contextEntry is not { } entry)
            return;
        if (entry.IsDirectory)
            NavigateInto(entry.Path);
        else
            OpenItem(entry.Path);
    }

    /// <summary>Deletes the real file/folder from disk (with confirmation) - the only destructive
    /// action a folder fence exposes, since every other "removal" a plain fence supports (drag off,
    /// remove from fence) doesn't apply here: there's no separate "in the fence" state to remove
    /// something from, only the real folder itself.</summary>
    private void ConfirmDeleteContextEntry()
    {
        if (_contextEntry is not { } entry)
            return;

        var kind = entry.IsDirectory ? "folder" : "file";
        var result = MessageBox.Show(this,
            $"Delete this {kind} from disk?\n\n{entry.Path}\n\nThis can't be undone.",
            "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
            return;

        try
        {
            if (entry.IsDirectory)
                Directory.Delete(entry.Path, recursive: true);
            else
                File.Delete(entry.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't delete \"{Path.GetFileName(entry.Path)}\": {ex.Message}",
                "Fence Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        RefreshEntries();
        RenderAndPresent();
    }

    /// <summary>The fence's name and its folder's name are kept in sync both ways: renaming the fence
    /// renames the real folder too (see TryRenameRootFolder), and a rename made in Explorer renames the
    /// fence (see FollowRename). Only a fence with no folder to rename (empty, missing, or a drive root)
    /// renames just its own label. A folder rename that fails leaves both names as they were.</summary>
    protected override string Title
    {
        get => _model.Name;
        set
        {
            if (_model.RootFolderPath is not null && !_rootMissing
                && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_model.RootFolderPath)) is not null)
            {
                TryRenameRootFolder(value);
                return;
            }
            _model.Name = value;
            _manager.Save();
        }
    }

    private void TryRenameRootFolder(string newName)
    {
        var root = Path.TrimEndingDirectorySeparator(_model.RootFolderPath!);
        var parent = Path.GetDirectoryName(root)!;
        // Windows silently drops trailing dots/spaces from a folder name - trimmed here so the fence
        // name matches what actually lands on disk.
        newName = newName.TrimEnd('.', ' ');

        string? problem = null;
        if (newName.Length == 0 || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            problem = "A folder name can't contain any of these characters:\n\\ / : * ? \" < > |";
        var newRoot = Path.Combine(parent, newName);
        var caseOnly = string.Equals(newRoot, root, StringComparison.OrdinalIgnoreCase);
        if (problem is null && !caseOnly && (Directory.Exists(newRoot) || File.Exists(newRoot)))
            problem = $"There's already a file or folder named \"{newName}\" in\n{parent}";

        // Success repoints and renames this fence (and any other inside the folder) via
        // ResumeAfterFolderRename.
        if (problem is null && !string.Equals(newRoot, root, StringComparison.Ordinal))
            problem = _manager.RenameFolder(root, newRoot);

        if (problem is not null)
            MessageBox.Show(this, $"Couldn't rename the folder \"{Path.GetFileName(root)}\".\n\n{problem}",
                "Rename Folder Fence", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>See FolderFenceManager.RenameFolder - stops this fence holding anything open that
    /// would block another fence's folder rename.</summary>
    internal void PauseWatchers() => DisposeWatchers();

    /// <summary>Counterpart to PauseWatchers. oldPath is null when the rename failed (nothing moved);
    /// otherwise a fence showing anything inside it follows it to newPath.</summary>
    internal void ResumeAfterFolderRename(string? oldPath, string newPath)
    {
        if (oldPath is not null && !_rootMissing && CurrentDirectory is { } current && IsSameOrUnder(current, oldPath))
            FollowRename(oldPath, newPath);
        else
            RestartWatcher();
    }

    protected override int TitleRowHeight => TitleBarHeight;

    // Hide Header would leave the folder tab (see PaintFolderTab) with nothing to attach to, so
    // there's no reason to offer turning it on - see the HideHeader override below, which also
    // makes sure it can never actually get turned on some other way (Copy Settings To from an
    // ordinary fence, this widget's own fence-to-folder-fence conversion, hand-edited JSON) either.
    // Light Border strokes just the header on its own, independent of the tab - an odd combination
    // now that the header's own outline is only ever part of the wider tab+body silhouette (see
    // GetBodyOutlinePath), not a standalone shape worth bordering by itself; unlike HideHeader, this
    // one needs no equivalent override - PaintChrome's own Light Border stroke is already gated on
    // ShowLightBorderOption itself (see that property's own doc comment), so a leaked
    // Style.LightBorder = true from any of those same sources just never renders, regardless of what
    // the model holds. Corner Radius capped lower than the base's own 50 - the tab/diagonal
    // proportions (see GetTabWidth/TabExtraHeight) are sized for a more modest range; a much larger
    // radius starts to visibly outgrow them.
    protected override bool ShowHideHeaderOption => false;
    protected override bool ShowLightBorderOption => false;
    protected override int CornerRadiusMax => 20;

    /// <summary>Forced off rather than merely defaulted off - unlike Light Border (see
    /// ShowLightBorderOption's own comment), HideHeader drives TitleVisible directly with no
    /// equivalent ShowHideHeaderOption gate anywhere in PaintChrome, so a leaked true would actually
    /// hide the header (tab included, see GetBodyOutlinePath's own !TitleVisible fallback) rather
    /// than just losing its menu row. Ignoring both the read (always false, regardless of whatever
    /// _model.HideHeader itself holds - including a pre-existing true left over on disk from before
    /// this override existed) and the write keeps this setting genuinely inert for a folder fence,
    /// rather than relying on every caller that might set HideHeader to know to skip it.</summary>
    protected override bool HideHeader
    {
        get => false;
        set { }
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

    /// <summary>"Change Folder..."/"Open in Explorer" (only once a root is set - the empty "+"
    /// state has nothing to change or open yet), then the same two "fence additionals" FenceForm
    /// offers - Hide Shortcut Names/OCD Fence Sizing plus its OCD Formatting submenu - which apply
    /// to this widget's grid the exact same way regardless of whether a folder is set yet.</summary>
    protected override IReadOnlyList<DropdownMenu.Row>? BuildAdditionalSettingsRows()
    {
        var rows = new List<DropdownMenu.Row>();

        if (_model.RootFolderPath is not null)
        {
            rows.Add(new DropdownMenu.Row(CmdChangeFolder, "Change Folder..."));
            rows.Add(new DropdownMenu.Row(CmdOpenInExplorer, "Open in Explorer"));
            rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        }

        rows.Add(new DropdownMenu.Row(CmdToggleHideLabels, "Hide Shortcut Names", HasCheckbox: true, IsChecked: () => _model.HideLabels));
        rows.Add(new DropdownMenu.Row(CmdToggleOcdSizing, "OCD Fence Sizing", HasCheckbox: true, IsChecked: () => _model.OcdFenceSizing,
            Tooltip: "After you resize this fence by hand, automatically snap it to the tightest size that fits its icons (same as OCD Formatting > Both)."));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        rows.Add(new DropdownMenu.Row(0, "OCD", Submenu: new List<DropdownMenu.Row>
        {
            new(TagFolderFenceDimensionsHeader, "Fence Dimensions", IsHeader: true),
            new(0, string.Empty, IsSeparator: true),
            new(CmdResizeBoth, "Both"),
            new(CmdResizeLeftRight, "Left/Right"),
            new(CmdResizeTopDown, "Top/Down"),
        }));

        return rows;
    }

    protected override void HandleSettingsCommand(int id)
    {
        switch (id)
        {
            case CmdChangeFolder: BrowseForFolder(); break;
            case CmdOpenInExplorer: OpenItem(_model.RootFolderPath); break;
            case CmdToggleHideLabels: ToggleHideLabels(); break;
            case CmdToggleOcdSizing: ToggleOcdFenceSizing(); break;
            case CmdResizeBoth: FormatDimensions(adjustWidth: true, adjustHeight: true); break;
            case CmdResizeLeftRight: FormatDimensions(adjustWidth: true, adjustHeight: false); break;
            case CmdResizeTopDown: FormatDimensions(adjustWidth: false, adjustHeight: true); break;
            default:
                base.HandleSettingsCommand(id);
                break;
        }
    }

    private void ToggleHideLabels()
    {
        _model.HideLabels = !_model.HideLabels;
        _manager.Save();
        // Changes EffectiveCellHeight, which OCD Fence Sizing's fit is based on - only height can
        // possibly need to change here, never the columns/width.
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
    /// wasted space around its current grid, keeping the top-left corner fixed - same approach as
    /// FenceForm's own FormatDimensions, just against _entries instead of _model.Files.</summary>
    private void FormatDimensions(bool adjustWidth, bool adjustHeight)
    {
        var contentSize = GetContentSize();
        if (contentSize.Width <= 0 || contentSize.Height <= 0 || _entries.Count == 0)
            return;

        var currentColumns = GetColumns(contentSize.Width);
        var columns = adjustWidth ? Math.Min(currentColumns, _entries.Count) : currentColumns;

        var availableHeight = Math.Max(0, contentSize.Height - GridTop - GridPadding * 2);
        var currentVisibleRows = Math.Max(1, (availableHeight + EffectiveCellHeight / 2) / EffectiveCellHeight);
        var totalRowsNeeded = (_entries.Count + columns - 1) / columns;
        var finalRows = adjustHeight ? Math.Min(currentVisibleRows, totalRowsNeeded) : currentVisibleRows;

        var newBounds = _model.Bounds;

        if (adjustWidth)
        {
            newBounds.Width = GridPadding * 2 + columns * CellWidth;
            if (finalRows < totalRowsNeeded)
                newBounds.Width += Scrollbar.Width + Scrollbar.Margin;
        }

        if (adjustHeight)
            newBounds.Height = GridTop + GridPadding * 2 + finalRows * EffectiveCellHeight;

        if (newBounds == _model.Bounds)
            return;

        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0,
            newBounds.Width + OuterMargin * 2, newBounds.Height + TopBand + BottomBand,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        _model.Bounds = newBounds;
        _manager.Save();
    }

    protected override void PersistStyle() => _manager.Save();

    protected override void DisposeOwnedResources()
    {
        DisposeWatchers();
        _refreshTimer.Dispose();
        _itemContextMenu?.Dispose();
        _dragGhost?.Dispose();
        foreach (var icon in _iconCache.Values)
            icon?.Dispose();
    }

    private static int GetColumns(int contentWidth) => Math.Max(1, (contentWidth - GridPadding * 2) / CellWidth);

    private int GetMaxScroll(int contentWidth, int contentHeight)
    {
        if (_entries.Count == 0)
            return 0;
        var columns = GetColumns(contentWidth);
        var rows = (_entries.Count + columns - 1) / columns;
        var availableHeight = Math.Max(0, contentHeight - GridTop - GridPadding * 2);
        return Math.Max(0, rows * EffectiveCellHeight - availableHeight);
    }

    private Rectangle GridViewport(int contentWidth, int contentHeight)
    {
        var trackTop = GridTop + GridPadding;
        var trackHeight = Math.Max(0, contentHeight - trackTop - GridPadding);
        return new Rectangle(0, trackTop, contentWidth, trackHeight);
    }

    private int? IndexAtGridPosition(Point contentLocation)
    {
        if (_entries.Count == 0)
            return null;

        var contentSize = GetContentSize();
        // Bounded against the actual visible/scrolled viewport, not just a Y >= GridTop floor - a
        // point below the grid (a bar button sitting in the margin band, say) would otherwise still
        // resolve to a real, scrolled-out-of-view index purely by arithmetic, wrongly hover-
        // highlighting an item nobody's actually pointing at.
        if (!GridViewport(contentSize.Width, contentSize.Height).Contains(contentLocation))
            return null;

        var columns = GetColumns(contentSize.Width);
        var column = (contentLocation.X - GridPadding) / CellWidth;
        var row = (contentLocation.Y - GridTop - GridPadding + _scrollbar.Offset) / EffectiveCellHeight;
        if (column < 0 || column >= columns || row < 0)
            return null;

        var index = row * columns + column;
        return index >= 0 && index < _entries.Count ? index : null;
    }

    private Icon? GetIcon(string path)
    {
        if (_iconCache.TryGetValue(path, out var cached))
            return cached;

        Icon? icon = null;
        try
        {
            icon = ShellIcons.ExtractLargeIcon(path) ?? Icon.ExtractAssociatedIcon(path);
        }
        catch (IOException)
        {
        }
        catch (System.Security.SecurityException)
        {
        }

        if (icon is not null)
            _iconCache[path] = icon;
        return icon;
    }

    private static void DrawImageWithOpacity(Graphics g, Image image, Rectangle rect, float opacity)
    {
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity }, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        g.DrawImage(image, rect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
    }

    private static string GetDisplayName(GridEntry entry) =>
        entry.IsDirectory ? Path.GetFileName(entry.Path) : Path.GetFileNameWithoutExtension(entry.Path);

    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        _scrollbar.ClampToMax(GetMaxScroll(contentWidth, contentHeight));

        // Body/title fill (tab included - see GetHeaderFillPath), border, title text, the
        // Settings/Copy Settings/"−"/"×" buttons, and their own hover tooltip are all
        // LayeredWidgetForm's own - this only draws what's genuinely folder-fence-specific: the
        // empty-state "+" or the item grid.
        PaintChrome(g, contentWidth, contentHeight);

        if (_currentSubPath is not null && TitleVisible)
            PaintBackButton(g);

        if (NeedsFolder)
            PaintEmptyState(g, contentWidth, contentHeight);
        else
            PaintItems(g, contentWidth, contentHeight);
    }

    /// <summary>Pen width/inset both scale off the glyph's own current rect (see EmptyStatePlusRect)
    /// rather than being fixed - a fixed 3px/6px pair sized for the old fixed 64px box read as
    /// thick/blurry once EmptyStatePlusSize shrank to 40, and would only get worse still on a fence
    /// small enough to shrink the rect itself further.</summary>
    private void PaintEmptyState(Graphics g, int contentWidth, int contentHeight)
    {
        var rect = ToWindow(EmptyStatePlusRect(new Size(contentWidth, contentHeight)));
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        var inset = Math.Min(6f, rect.Width * 0.15f);
        var half = rect.Width / 2f - inset;
        var penWidth = Math.Clamp(rect.Width * 0.06f, 1.5f, 3f);

        var previousSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.FromArgb(160, 255, 255, 255), penWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(pen, cx - half, cy, cx + half, cy);
            g.DrawLine(pen, cx, cy - half, cx, cy + half);
        }
        g.SmoothingMode = previousSmoothing;

        // A fence whose folder has vanished says so under the "+", rather than looking like a
        // freshly-created one - the header still shows its old name.
        if (_rootMissing)
        {
            var noteTop = rect.Bottom + 6;
            var noteRect = ToWindow(new Rectangle(GridPadding, 0, contentWidth - GridPadding * 2, 0));
            noteRect.Y = noteTop;
            noteRect.Height = ToWindow(new Point(0, contentHeight - GridPadding)).Y - noteTop;
            if (noteRect.Height > 0)
            {
                var previousTextHint = g.TextRenderingHint;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (var textBrush = new SolidBrush(Color.FromArgb(200, 245, 245, 245)))
                using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.LineLimit })
                    g.DrawString($"Folder not found:\n{_model.RootFolderPath}", Font, textBrush, noteRect, textFormat);
                g.TextRenderingHint = previousTextHint;
            }
        }
    }

    private void PaintItems(Graphics g, int width, int height)
    {
        if (_entries.Count == 0)
            return;

        g.SetClip(ToWindow(new Rectangle(0, GridTop, width, height - GridTop)), CombineMode.Intersect);
        var columns = GetColumns(width);

        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var isDragSource = _draggingEntry is { } dragging && string.Equals(entry.Path, dragging.Path, StringComparison.OrdinalIgnoreCase);
            var column = i % columns;
            var row = i / columns;
            var cellX = GridPadding + column * CellWidth;
            var cellY = GridTop + GridPadding + row * EffectiveCellHeight - _scrollbar.Offset;

            if (cellY + EffectiveCellHeight <= GridTop || cellY >= height)
                continue;

            if (i == _hoverIndex && !isDragSource)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
                using var hoverPath = RoundedRectPath.Full(ToWindow(new Rectangle(cellX, cellY, CellWidth, EffectiveCellHeight)), 4);
                g.FillPath(hoverBrush, hoverPath);
            }

            var iconX = cellX + (CellWidth - IconSize) / 2;
            var iconRect = ToWindow(new Rectangle(iconX, cellY + IconTopPadding, IconSize, IconSize));
            if (GetIcon(entry.Path) is { } icon)
            {
                using var bitmap = icon.ToBitmap();
                // Faded in place while it's being dragged - the ghost near the cursor (see
                // OnMouseMove) is what's actually "held", same treatment as FenceForm's own
                // DrawImageWithOpacity.
                if (isDragSource)
                    DrawImageWithOpacity(g, bitmap, iconRect, 0.35f);
                else
                    g.DrawImage(bitmap, iconRect);
            }

            if (_model.HideLabels)
                continue;

            var labelTop = cellY + IconTopPadding + IconSize + 2;
            var labelHeight = EffectiveCellHeight - IconTopPadding - IconSize - 2;
            if (labelTop >= GridTop)
            {
                var visibleHeight = Math.Min(labelHeight, height - labelTop);
                if (visibleHeight > 0)
                {
                    var labelRect = ToWindow(new Rectangle(cellX, labelTop, CellWidth, visibleHeight));
                    var previousTextHint = g.TextRenderingHint;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    using (var textBrush = new SolidBrush(Color.WhiteSmoke))
                    using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit })
                        g.DrawString(GetDisplayName(entry), Font, textBrush, labelRect, textFormat);
                    g.TextRenderingHint = previousTextHint;
                }
            }
        }

        if (_scrollbar.GetGeometry(GridViewport(width, height), GetMaxScroll(width, height)) is { } sb)
            PaintScrollbar(g, sb);
    }

    /// <summary>The header's own back button - a horizontal shaft with an arrowhead at its left end
    /// (an actual "&lt;-" shape, not just a bare chevron), same hand-drawn-glyph convention as
    /// WidgetManagerWidget's own PaintPlusGlyph/PaintCogGlyph (no icon asset library in this app) -
    /// not gated by ShowsButtons, since this is core navigation, not extra chrome.</summary>
    private void PaintBackButton(Graphics g)
    {
        var rect = ToWindow(BackButtonRect());
        var cy = rect.Y + rect.Height / 2f;
        var shaftHalfLength = rect.Width * 0.32f;
        var headSize = rect.Width * 0.24f;
        var left = rect.X + rect.Width / 2f - shaftHalfLength;
        var right = rect.X + rect.Width / 2f + shaftHalfLength;

        var previousSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.WhiteSmoke, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLine(pen, left, cy, right, cy);
        g.DrawLine(pen, left + headSize, cy - headSize, left, cy);
        g.DrawLine(pen, left + headSize, cy + headSize, left, cy);
        g.SmoothingMode = previousSmoothing;
    }
}
