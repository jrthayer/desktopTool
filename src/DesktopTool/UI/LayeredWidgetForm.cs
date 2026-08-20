using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Snapping;
using DesktopTool.Native;

namespace DesktopTool.UI;

/// <summary>
/// Base class for a hand-painted, layered Win32 window (WS_POPUP + WS_EX_LAYERED, no WinForms child
/// controls) that behaves like a Fence: draggable and resizable via the OS's own interactive move/
/// resize loop, snapping against other fences and custom snap lines by default, styled from a live
/// tint/opacity, with a rename-able title row and a Settings button living in a band that flips
/// between the top and bottom depending on how close to a monitor's own top edge the window is
/// sitting. Every one of those - move, resize, snap, rename, the Settings button and its dropdown -
/// works out of the box with no subclass code beyond the small hooks below (what the title text is,
/// what rows the Settings dropdown shows, what a handful of theme colors are); a subclass only needs
/// its own WndProc/geometry for whatever makes it *not* just chrome (a Fence's icon grid, its own
/// resize-vs-move activation rules) - see FenceForm for the fullest example.
/// </summary>
internal abstract class LayeredWidgetForm : Form
{
    protected const int WM_NCHITTEST = 0x0084;
    protected const int WM_NCLBUTTONDBLCLK = 0x00A3;
    protected const int WM_PAINT = 0x000F;
    protected const int WM_ERASEBKGND = 0x0014;
    protected const int WM_SIZE = 0x0005;
    protected const int WM_RBUTTONUP = 0x0205;
    protected const int WM_ENTERSIZEMOVE = 0x0231;
    protected const int WM_EXITSIZEMOVE = 0x0232;

    protected const int HTCLIENT = 1;
    protected const int HTCAPTION = 2;
    protected const int HTLEFT = 10;
    protected const int HTRIGHT = 11;
    protected const int HTTOP = 12;
    protected const int HTTOPLEFT = 13;
    protected const int HTTOPRIGHT = 14;
    protected const int HTBOTTOM = 15;
    protected const int HTBOTTOMLEFT = 16;
    protected const int HTBOTTOMRIGHT = 17;
    // Non-client, but not a caption - DefWindowProc's own default WM_NCLBUTTONDOWN handling starts a
    // move only for HTCAPTION specifically, so a subclass returning this instead for its title row
    // keeps it non-client for everything else that depends on that (right-click/double-click routing,
    // hover tracking - see this class's own WndProc, none of which require HTCAPTION specifically)
    // while no longer letting a left-button drag from there move the window.
    protected const int HTBORDER = 18;

    // Deliberately never tinted - exists purely so Windows doesn't treat the margin band as
    // click-through (alpha 0 pixels of a layered window are click-through; alpha 1 is the practical
    // minimum that still isn't). Painted first, under everything opaque a subclass draws on top.
    protected static readonly Color MarginFillColor = Color.FromArgb(1, 0, 0, 0);

    protected static uint ColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    // Only shows engagement chrome (a settings button, an active-state border) while actually
    // engaged - right-click anywhere, or a title-bar click - see WidgetActivation's own doc comment.
    protected readonly WidgetActivation Activation = new();

    /// <summary>Lets CopySettingsOverlay drive this widget's own engaged chrome (Settings button,
    /// active-state border) as a hover-target indicator, without a real OS focus/mouse event ever
    /// reaching it - the overlay owns all input for the duration of a pick (see FindAt's own doc
    /// comment), so this is the only way the widget can visually read as "this is what a click would
    /// target right now" while that's happening.</summary>
    internal void SetPickTargetActive(bool active)
    {
        if (active)
            Activation.Activate();
        else
            Activation.Deactivate();
    }

    // Eases the render opacity toward TargetOpacity over several ticks rather than jumping there in
    // one repaint - see OpacityAnimator's own doc comment for why a plain Form.Opacity can't do this
    // for a window that pushes its own bitmap via UpdateLayeredWindow. Named RenderOpacity, not just
    // Opacity, so it doesn't shadow Form's own same-named (and differently-typed) property.
    protected readonly OpacityAnimator RenderOpacity;

    // Every fence/snap-line-aware widget in this app shares the one FenceManager instance for its
    // snapping - see ComputeMovedBody/ComputeResizedBody/BeginSnapDrag below, all of which use this
    // directly so snapping works out of the box for any subclass, not just FenceForm.
    protected readonly FenceManager Fences;

    // Covers both an interactive move and an interactive resize - set between WM_ENTERSIZEMOVE and
    // WM_EXITSIZEMOVE.
    protected bool IsMoving { get; set; }

    // Whether the in-progress drag (see IsMoving) is specifically a resize rather than a move - set
    // from OnNcLButtonDown's own default (see its own comment), read back by BeginSnapDrag/a
    // subclass's own OnDragEnd to tell the two apart.
    protected bool IsResizing { get; set; }

    // Together back "Full Opacity When Active" (see IsHovered/TargetOpacity) - split into
    // client/non-client because they're detected two completely different ways: OnMouseEnter/
    // OnMouseLeave for the client half, WM_NCMOUSEMOVE/WM_NCMOUSELEAVE below for the margin band.
    private bool _isClientHovered;
    private bool _isNonClientHovered;
    protected bool IsHovered => _isClientHovered || _isNonClientHovered;

    // Which of the base's own buttons (if any) the cursor currently sits over - drives the same
    // translucent-white hover tint a Fence's own icon-grid cells use (see PaintButtonHoverTint),
    // just for Settings/ChromeButton/ContentButton instead. Recomputed on every OnMouseMove/
    // OnMouseLeave (see UpdateButtonHover), repainting only on an actual change.
    private enum HoveredButtonKind { None, Settings, Extra, Content, HeaderClose }
    private HoveredButtonKind _hoveredButtonKind = HoveredButtonKind.None;
    private int _hoveredButtonIndex = -1;

    // "Copy Settings To" is icon-only (a hand-drawn eyedropper glyph, no text label - see
    // PaintEyedropperGlyph), so it needs its own tooltip the same way an ExtraButtons' own
    // ChromeButton.Tooltip does - shared between the two rather than a second copy of it, since only
    // one of them can ever be hovered at once. Shown/hidden from the same UpdateButtonHover pass that
    // already drives every base button's hover tint.
    private readonly PaintedTooltip _chromeButtonTooltip = new();

    // Fixed anchor a drag/resize measures against every tick, instead of trusting the OS's own
    // incrementally-proposed rect (drift/stickiness otherwise) - captured once, from GetCurrentBody,
    // right as WM_ENTERSIZEMOVE fires.
    protected Point LeftDragStartScreenPoint { get; set; }
    protected Rectangle DragStartBody { get; private set; }

    // Guards RenderAndPresent against a reentrant repaint triggered mid-teardown - destroying the
    // native window as part of Dispose synchronously delivers WM_ACTIVATE while WndProc is still
    // hooked up, reaching OnDeactivate -> RenderAndPresent before Dispose(true) even returns.
    protected bool IsDisposing { get; private set; }

    // Backs the rename EditBox's WM_CTLCOLOREDIT background and any native owner-draw popup menu a
    // subclass builds - one shared themed native brush, recreated on demand (see GetThemeBrush)
    // rather than fixed for the window's whole lifetime, since the color it themes to can change at
    // runtime.
    private IntPtr _themeBrush = IntPtr.Zero;
    private Color _themeBrushColor;

    // Backs TitleFont - recreated only when Style.TitleFontSize actually changes, same reasoning as
    // _themeBrush/_themeBrushColor above (a fresh Font/GDI handle on every single repaint would be
    // both wasteful and, left undisposed between repaints, a real handle leak).
    private Font? _titleFont;
    private int _titleFontSize = -1;

    // True when the button row currently belongs on the bottom band instead of the top - see
    // ComputeButtonRowAtBottom. Kept in sync wherever a subclass's own position is computed/changed
    // (its own CreateParams, and every tick of a live drag) rather than read fresh on every use.
    protected bool ButtonRowAtBottom { get; set; }

    // Tracks the Settings button's own left/right flip (see ShouldSettingsButtonOpenLeft) through a
    // live move - unlike ButtonRowAtBottom above (whose flip changes the window's own outer bounds,
    // so becomes visible for free as the OS moves it), a left/right flip only changes where the
    // button paints *within* the content, so WM_MOVING needs to explicitly repaint whenever this
    // actually changes rather than relying on the OS to show it. Checked every tick, but only
    // triggers a repaint on an actual change - same restraint as everywhere else that avoids a full
    // repaint on every single mouse-move message. Only relevant during a move, not a resize - resize
    // can only start while inactive (see HitTest), and the Settings button isn't shown then anyway.
    private bool _draggedSettingsButtonOnLeft;

    // The rename EditBox and the title row's own right-click "Rename" menu - both base-owned now
    // (see BeginRename/ShowTitleContextMenu's own defaults) so a subclass gets renaming for free.
    private EditBox? _renameBox;
    private ContextMenuStrip? _titleContextMenu;
    protected bool IsRenaming => _renameBox is not null;

    // The currently-open Settings dropdown, or null - see OpenSettingsMenu.
    protected DropdownMenu? SettingsDropdown { get; private set; }

    // Every live LayeredWidgetForm on screen right now - a fence, the Layout Launcher, any future
    // widget built on this base - registered/unregistered in the constructor/Dispose below. Lets
    // GetOtherWidgetEdges gather snap candidates from every OTHER widget generically, without any
    // one widget type (FenceManager, say) needing to know the others exist or hold a bolted-on
    // bounds-tracking property per widget type.
    private static readonly List<LayeredWidgetForm> _liveWidgets = new();

    /// <summary>Every live widget, for CopySettingsGroupPicker's own "All Widgets"/"All Fences"/"All
    /// Non-Fence Widgets" boxes - a read-only view of the same list FindAt already walks, rather than
    /// a second bolted-on collection to keep in sync.</summary>
    internal static IReadOnlyList<LayeredWidgetForm> LiveWidgets => _liveWidgets;

    /// <summary>Which live widget (if any) screenPoint currently sits over - used by
    /// CopySettingsOverlay for continuous hover-tracking while it's itself the topmost window on
    /// screen, which rules out the usual WindowFromPoint+GetAncestor technique
    /// (WindowPickerOverlay/EyedropperOverlay's own click-time approach): that would only ever see
    /// the overlay's own handle, since nothing can be topmost above a full-screen click-catcher.
    /// Geometric containment against each widget's own live GetWindowRect instead - no P/Invoke
    /// beyond what NativeMethods already exposes. Known, accepted limitation: two widgets
    /// overlapping at screenPoint aren't Z-order-resolved, just first-match in _liveWidgets' own
    /// (insertion) order - the same kind of rare-edge-case tradeoff this app already documents
    /// elsewhere rather than fully solving.</summary>
    internal static LayeredWidgetForm? FindAt(Point screenPoint)
    {
        foreach (var widget in _liveWidgets)
        {
            if (!widget.Visible)
                continue;
            if (NativeMethods.GetWindowRect(widget.Handle, out var rect)
                && screenPoint.X >= rect.Left && screenPoint.X < rect.Right
                && screenPoint.Y >= rect.Top && screenPoint.Y < rect.Bottom)
            {
                return widget;
            }
        }
        return null;
    }

    protected LayeredWidgetForm(float initialOpacity, FenceManager fences)
    {
        Fences = fences;
        RenderOpacity = new OpacityAnimator(initialOpacity, () => TargetOpacity, RenderAndPresent);
        Activation.Changed += RenderAndPresent;
        _liveWidgets.Add(this);
    }

    /// <summary>Lazily (re)creates the shared native brush only when color has actually changed since
    /// the last call - both the rename EditBox's WM_CTLCOLOREDIT and native owner-draw menu theming
    /// can fire often enough (every redraw, every menu open) that recreating a GDI brush on every
    /// single call would be wasteful.</summary>
    protected IntPtr GetThemeBrush(Color color)
    {
        if (_themeBrush == IntPtr.Zero || _themeBrushColor != color)
        {
            if (_themeBrush != IntPtr.Zero)
                NativeMethods.DeleteObject(_themeBrush);
            _themeBrush = NativeMethods.CreateSolidBrush(ColorRef(color));
            _themeBrushColor = color;
        }
        return _themeBrush;
    }

    /// <summary>bodyScreenLocation is the widget's visible body's own top-left corner in screen
    /// coordinates. True when placing bandHeightAtTop above it would extend above its monitor's own
    /// working area - in that case the button row belongs on the bottom band instead, so the widget
    /// can still sit flush with the very top of the screen without its own buttons going unreachably
    /// off-screen.</summary>
    protected static bool ComputeButtonRowAtBottom(Point bodyScreenLocation, int bandHeightAtTop) =>
        bodyScreenLocation.Y - bandHeightAtTop < Screen.FromPoint(bodyScreenLocation).WorkingArea.Top;

    /// <summary>What WM_MOVING/WM_SIZING (and each subclass's own CreateParams) actually call to
    /// refresh ButtonRowAtBottom for body's current position - ComputeButtonRowAtBottom(body.Location,
    /// MaxTopBand) by default, the same top-overflow check every widget on this base has always used.
    /// A subclass whose button row instead defaults to the BOTTOM band, flipping to the top only when
    /// there wouldn't be room below (FolderFenceForm's own folder tab always needs its own reserved
    /// space up top regardless of where the button row is - see TopBand/SettingsButtonRowInset -
    /// so the usual "default top, flip to bottom" trade only kicks in when overflowing the bottom
    /// instead) overrides this with the mirrored check, using body's own Height too (which the
    /// simpler Point-based helper above never needed, since a plain top-overflow check never cared
    /// how tall body was).</summary>
    protected virtual bool ComputeButtonRowAtBottomFor(Rectangle body) =>
        ComputeButtonRowAtBottom(body.Location, MaxTopBand);

    protected static Point ScreenLParamToWindowPoint(IntPtr lParam, RECT windowRect)
    {
        long l = lParam.ToInt64();
        short screenX = (short)(l & 0xFFFF);
        short screenY = (short)((l >> 16) & 0xFFFF);
        return new Point(screenX - windowRect.Left, screenY - windowRect.Top);
    }

    // The invisible drag/resize-grab band around the visible body (constant on every edge but one),
    // and the margin band on whichever of top/bottom currently holds the button row vs. doesn't - see
    // ButtonRowAtBottom. Left fully to each subclass: FenceForm's own split (TopBand collapsing to 0
    // once flipped, BottomBand flooring at OuterMargin rather than 0) is Fence-specific reasoning
    // about Fence's own margin band, not something to generalize from a single example.
    protected abstract int OuterMargin { get; }
    protected abstract int TopBand { get; }
    protected abstract int BottomBand { get; }

    /// <summary>The band height at the top edge when the button row is NOT flipped to the bottom -
    /// i.e. what TopBand equals when ButtonRowAtBottom is false. Needed as its own hook (distinct
    /// from TopBand, which already reflects whichever state is currently true) so WM_MOVING/
    /// WM_SIZING can decide whether a given tick's new position crosses the flip threshold, the same
    /// way ComputeButtonRowAtBottom always has.</summary>
    protected abstract int MaxTopBand { get; }

    protected Point ToContent(Point windowPoint) => new(windowPoint.X - OuterMargin, windowPoint.Y - TopBand);
    protected Point ToWindow(Point contentPoint) => new(contentPoint.X + OuterMargin, contentPoint.Y + TopBand);
    protected Rectangle ToWindow(Rectangle contentRect) =>
        new(contentRect.X + OuterMargin, contentRect.Y + TopBand, contentRect.Width, contentRect.Height);

    /// <summary>Window-relative (e.g. already run through ToWindow) to screen coordinates - needed
    /// for EditBox, which (unlike everything else drawn here) is a real top-level window rather than
    /// something painted into this window's own layered bitmap.</summary>
    protected Rectangle ToScreen(Rectangle windowRect) => new(PointToScreen(windowRect.Location), windowRect.Size);

    /// <summary>The visible body's own size - the actual (padded) window size minus OuterMargin on
    /// the left/right/non-button-row side and TopBand/BottomBand's button-row side - all content/
    /// hit-test math is in this "content" space.</summary>
    protected Size GetContentSize()
    {
        NativeMethods.GetClientRect(Handle, out var clientRect);
        return new Size(Math.Max(0, clientRect.Right - OuterMargin * 2), Math.Max(0, clientRect.Bottom - TopBand - BottomBand));
    }

    // ---- Style / theme ----
    //
    // Every widget on this base is styled from a live IWidgetStyle (tint color, header darkness,
    // opacity, full-opacity-on-hover, tint strength, snap margin) - a fence's own FenceModel and the
    // Layout Launcher's own LayoutLauncherModel both already implement it. Base owns the derivation
    // (ThemedBody/ThemedTitle/etc below) and the generic Settings-dropdown rows (Hide Header, Full
    // Opacity When Active, the shared color grid/sliders/margin stepper) that follow from it, so a
    // subclass with nothing further to add (Layout Launcher, chrome-only for now) gets a fully
    // working Settings menu for free; one with more to show (a Fence's own Hide Shortcut
    // Names/OCD Sizing) overrides BuildSettingsRows/HandleSettingsCommand entirely rather than
    // patching the default, but can still reuse the same shared Cmd* ids/mutator hooks below for the
    // rows it keeps.

    /// <summary>The subclass's own per-element styling knobs - a fence passes its FenceModel, the
    /// Layout Launcher its LayoutLauncherModel, both already implementing this.</summary>
    protected abstract IWidgetStyle Style { get; }

    /// <summary>Whether the entire title row is currently hidden (reclaiming its space for content,
    /// not just blanking its text) - a plain settable flag most of a subclass's own persisted model
    /// already has (FenceModel.HideHeader, LayoutLauncherModel.HideHeader), not part of IWidgetStyle
    /// itself since it affects the title row rather than tint/opacity. The setter is responsible for
    /// persisting, the same way Title's own setter is.</summary>
    protected abstract bool HideHeader { get; set; }

    protected virtual bool TitleVisible => !HideHeader;

    /// <summary>Whether an always-visible "×" close glyph paints in the title row itself (see
    /// PaintChrome/GetHeaderCloseButtonRect), independent of ShowsButtons - unlike the Settings/
    /// CopySettings/Extra button band, which only appears once the widget is "engaged" (right-click
    /// or title click), this is meant to be reachable without that first step. Same
    /// persisted-per-subclass-model shape as HideHeader above.</summary>
    protected abstract bool ShowHeaderCloseButton { get; set; }

    // Fallback palette for an untinted element - virtual so a future subclass wanting a genuinely
    // different base look could override one, but both current subclasses share these exact values
    // (an intentional part of the styling-unification effort, not a coincidence).
    protected virtual Color DefaultBodyColor => Color.FromArgb(255, 32, 32, 36);
    protected virtual Color DefaultBorderColor => Color.FromArgb(255, 70, 70, 78);
    protected virtual Color DefaultAccentColor => Color.FromArgb(255, 190, 190, 195);
    protected virtual Color DefaultMenuSelectedColor => Color.FromArgb(255, 55, 55, 62);
    protected virtual Color DefaultCheckboxBorderColor => Color.FromArgb(255, 150, 150, 158);

    protected Color? CurrentTint => Style.TintColor is { } argb ? Color.FromArgb(argb) : null;

    /// <summary>Full-strength version of the element's own tint (falling back to DefaultAccentColor)
    /// for anything that needs to read clearly rather than just hint at the theme - the active-state
    /// border, the Settings button, and the Settings dropdown's own checkmarks/selection ring.</summary>
    protected Color Accent => CurrentTint ?? DefaultAccentColor;

    /// <summary>Style.TintStrength (0-100%) as the 0.0-1.0 fraction StyleTint.Tint's amount parameter
    /// needs.</summary>
    protected double TintAmount => Style.TintStrength / 100.0;

    /// <summary>Only meaningful when Style.TintIsExact - dilutes an Eyedropper pick back toward
    /// DefaultBodyColor by TintAmount, the *reverse* direction from the regular Tint(base, tint,
    /// amount) call (there, amount=0 means "ignore the pick"; here, amount=0 means "keep the pick
    /// exact").</summary>
    protected Color DilutedExactTint(Color exact) => StyleTint.Tint(exact, DefaultBodyColor, TintAmount);

    /// <summary>Blends a preset/Custom... pick into DefaultBodyColor at TintAmount same as always; an
    /// Eyedropper pick (TintIsExact) instead starts from the exact color and dilutes it toward
    /// DefaultBodyColor by that same TintAmount (see DilutedExactTint) - both directions read as "how
    /// much of the picked color survives" even though the blend runs opposite ways under the hood.</summary>
    protected Color ThemedBody => Style.TintIsExact && CurrentTint is { } exactBodyTint
        ? DilutedExactTint(exactBodyTint)
        : StyleTint.Tint(DefaultBodyColor, CurrentTint, TintAmount);

    /// <summary>Always StyleTint.SafeChromeBlend, even when Style.TintIsExact or Tint Strength is
    /// turned all the way up - unlike ThemedBody/Accent, anything drawing fixed WhiteSmoke text or
    /// glyphs on top of a fill (the Settings dropdown, its tooltips, the Settings button) needs to
    /// stay readable no matter how light/bright the element's own picked color is.</summary>
    protected Color ChromeFill => StyleTint.Tint(DefaultBodyColor, CurrentTint, StyleTint.SafeChromeBlend);

    /// <summary>This base's own chrome fills - the Settings/ChromeButton/ContentButton backgrounds
    /// (see PaintSettingsButton/PaintExtraButtons/PaintContentButtons). Tinted at the same fixed
    /// SafeChromeBlend as ChromeFill rather than the user's adjustable Tint Strength, for the same
    /// "stay readable no matter how light/bright the pick is" reason ChromeFill's own doc comment
    /// gives - a button's fixed WhiteSmoke label needs to stay legible regardless of tint. Lightened
    /// off DefaultBodyColor itself (see StyleTint.LightenTowardWhite), not a fixed AppTheme gray, so
    /// it's guaranteed lighter than whatever this widget's own body color actually is rather than
    /// just happening to be lighter than one particular default. NOT used for list row banding - see
    /// ThemedListRow for that, which deliberately does track Tint Strength instead.</summary>
    protected Color ThemedField => StyleTint.Tint(StyleTint.LightenTowardWhite(DefaultBodyColor, 0.15), CurrentTint, StyleTint.SafeChromeBlend);

    /// <summary>The darker half of the ThemedField pair - see its own doc comment.</summary>
    protected Color ThemedFieldDark => StyleTint.Tint(AppTheme.FieldDark, CurrentTint, StyleTint.SafeChromeBlend);

    /// <summary>A list's own alternating row background (Layout Launcher's/Widget Manager's own
    /// PaintListRow) - alternates between this and ThemedListRowDark. Unlike ThemedField (this
    /// base's own button fills, which stay pinned to a fixed SafeChromeBlend so a button's fixed
    /// label text never goes unreadable), a list's own rows are part of the widget's main content,
    /// not auxiliary chrome - previously reused ThemedField for this too, but that meant Tint
    /// Strength visibly changing the body/header fill while the row banding sitting right next to it
    /// never moved read as broken rather than intentional. Tracks TintAmount the same way ThemedBody
    /// does, including the same TintIsExact dilute-from-exact reversal for an Eyedropper pick (see
    /// ThemedBody's own doc comment for why that reversal exists). Lightened off DefaultBodyColor
    /// itself, same reasoning as ThemedField.</summary>
    protected Color ThemedListRow
    {
        get
        {
            var lightened = StyleTint.LightenTowardWhite(DefaultBodyColor, 0.15);
            return Style.TintIsExact && CurrentTint is { } exact
                ? StyleTint.DilutedExact(exact, lightened, TintAmount)
                : StyleTint.Tint(lightened, CurrentTint, TintAmount);
        }
    }

    /// <summary>The darker half of the ThemedListRow pair - see its own doc comment.</summary>
    protected Color ThemedListRowDark => Style.TintIsExact && CurrentTint is { } exactDark
        ? StyleTint.DilutedExact(exactDark, AppTheme.FieldDark, TintAmount)
        : StyleTint.Tint(AppTheme.FieldDark, CurrentTint, TintAmount);

    private Color HeaderBaseColor => StyleTint.DarkenTowardBlack(DefaultBodyColor, Style.HeaderDarkness / 100.0);

    /// <summary>Tints HeaderBaseColor same as every other Themed* color, but with TintAmount's own
    /// strength shrinking as HeaderDarkness rises, reaching true black at 100% darkness regardless of
    /// tint - see FenceForm's original doc comment on this (now-shared) formula for the full
    /// reasoning. An exact Eyedropper pick darkens its own already-diluted ThemedBody color instead.</summary>
    protected Color ThemedTitle
    {
        get
        {
            var darkness = Style.HeaderDarkness / 100.0;
            if (Style.TintIsExact && CurrentTint is { } exactTitleTint)
                return StyleTint.DarkenTowardBlack(DilutedExactTint(exactTitleTint), darkness);
            return StyleTint.Tint(HeaderBaseColor, CurrentTint, TintAmount * (1 - darkness));
        }
    }

    protected Color ThemedBorder => StyleTint.Tint(DefaultBorderColor, CurrentTint, TintAmount);

    // SafeChromeBlend, not TintAmount - same fixed-WhiteSmoke-text-needs-to-stay-readable reasoning
    // as ChromeFill.
    protected Color ThemedMenuSelected => StyleTint.Tint(DefaultMenuSelectedColor, CurrentTint, StyleTint.SafeChromeBlend);
    protected Color ThemedCheckboxBorder => StyleTint.Tint(DefaultCheckboxBorderColor, CurrentTint, 0.4);

    // Translucent rather than opaque - a fully opaque accent border reads as too heavy/saturated
    // against a tinted body beneath it.
    protected Color ThemedActiveBorder => Color.FromArgb(220, Accent);

    /// <summary>Border width/visibility while the widget is engaged (see ShowsButtons) - a plain
    /// 1px ThemedBorder otherwise. Virtual so a subclass could tune it, though both current ones use
    /// the same value.</summary>
    protected virtual float ActiveBorderWidth => 8f;

    /// <summary>The outer body border's actual stroke width for the current state - ActiveBorderWidth
    /// while engaged, a plain 1px otherwise. Its own hook (rather than PaintChrome just inlining this
    /// ternary) so a subclass whose own GetBodyOutlinePath includes a non-orthogonal segment
    /// (FolderFenceForm's diagonal tab cut) can bump it slightly across the board - GDI+ renders a
    /// diagonal stroke with visibly less antialiased coverage than a horizontal/vertical one at the
    /// same nominal width, so the tab's own edge reads as thinner than the rest of the same,
    /// single-stroked path unless this compensates.</summary>
    protected virtual float BorderWidth => ShowsButtons ? ActiveBorderWidth : 1f;

    /// <summary>Only shows engagement chrome (the Settings button, an active-state border) while
    /// actually engaged - see WidgetActivation's own doc comment for why right-click/title-click
    /// specifically, not plain OS focus.</summary>
    protected bool ShowsButtons => Activation.ShouldShow;

    protected virtual float TargetOpacity =>
        Style.FullOpacityOnHover && (IsHovered || IsMoving || SettingsDropdown is not null) ? 1f : Style.Opacity / 100f;

    protected virtual Color EditBoxTextColor => Color.WhiteSmoke;
    protected virtual Color EditBoxBackgroundColor => ThemedBody;
    protected virtual Color ChromeMenuFieldColor => ChromeFill;
    protected virtual Color ChromeMenuHoverColor => ThemedMenuSelected;

    protected virtual Color SettingsMenuFieldColor => ChromeFill;
    protected virtual Color SettingsMenuHoverColor => ThemedMenuSelected;
    protected virtual Color SettingsMenuAccentColor => Accent;
    protected virtual Color SettingsMenuBorderColor => ThemedCheckboxBorder;

    // Blended from black rather than DefaultBodyColor (unlike every other Themed* color) - black for
    // an untinted element, and leaning more visibly toward a tinted one's own color at the same blend
    // amount than starting from dark gray would, since there's more contrast for Tint() to work with.
    protected virtual Color SettingsMenuTooltipColor => StyleTint.Tint(Color.Black, CurrentTint, StyleTint.SafeChromeBlend);

    // Shared Settings-dropdown command ids - negative, so they can never collide with a subclass's
    // own positive-numbered command ids (a Fence's CmdToggleHideLabels, CmdToggleOcdSizing, etc.)
    // without either side needing to know about the other's numbering.
    protected const int CmdToggleHideHeader = -1;
    protected const int CmdToggleFullOpacityOnHover = -2;
    protected const int CmdColorDefault = -3;
    protected const int CmdColorCustom = -4;
    protected const int CmdColorEyedrop = -5;
    protected const int CmdToggleHeaderBorderMode = -6;
    protected const int CmdToggleLightBorder = -7;
    protected const int CmdToggleHeaderCloseButton = -8;
    // Reserves -1000..-901 (100 ids) for the color-preset grid.
    protected const int CmdColorPresetBase = -1000;

    // Every IWidgetStyle property is mutated directly against Style (it IS the subclass's own model -
    // a Fence's FenceModel sits in FenceManager's own _models list by reference, so writing through
    // Style already reaches it) - the only thing that differs by subclass is how the change actually
    // reaches disk, which is exactly what this one hook is for. A Fence's own PersistStyle is a
    // one-liner (_manager.Save()); nothing here needs a dedicated SetHeaderDarkness/SetOpacity/etc.
    // abstract method of its own anymore, the same reason Title's own setter used to be the only
    // exception - now it's the pattern.
    protected abstract void PersistStyle();

    /// <summary>Applies a Settings-dropdown color pick - Default/preset/Custom... (exact: false) blend
    /// toward the plain theme and reset HeaderDarkness/Opacity/TintStrength back to their defaults
    /// (the sliders are a per-pick tweak, not a setting that carries over once you've moved to a
    /// different pick); an Eyedropper pick (exact: true) applies at full strength and instead resets
    /// Opacity to 100/TintStrength to 0, so it starts out pixel-exact - see ThemedBody/ThemedTitle's
    /// own TintIsExact branch for what "applies at full strength" actually means to render.</summary>
    private void ApplyTintPick(Color? color, bool exact)
    {
        Style.TintColor = color?.ToArgb();
        Style.TintIsExact = exact;
        if (exact)
        {
            Style.Opacity = 100;
            Style.TintStrength = 0;
        }
        else
        {
            Style.HeaderDarkness = WidgetStyleModel.DefaultHeaderDarkness;
            Style.Opacity = WidgetStyleModel.DefaultOpacity;
            Style.TintStrength = WidgetStyleModel.DefaultTintStrength;
        }
        PersistStyle();
        RenderOpacity.SnapToTarget();
        RenderAndPresent();
    }

    /// <summary>"Copy Settings To" (see CopySettingsOverlay) - applies source's own Base settings
    /// (every IWidgetStyle property, plus HideHeader/ShowHeaderCloseButton - the Form's own abstract
    /// properties, not part of IWidgetStyle, but still Base-flyout settings) onto this widget,
    /// entirely through the Style/HideHeader/ShowHeaderCloseButton interfaces, so no downcast to any
    /// concrete model type is needed here. Deliberately
    /// never touches position/size/title text/Visible - those are this widget's own identity/
    /// placement, not "settings" a paint-bucket tool should overwrite.
    ///
    /// Additional settings (a Fence's own Hide Shortcut Names/OCD Sizing, Layout Launcher's own Rows
    /// Shown/Always Max Rows) only come along when source is the exact same concrete widget type -
    /// see CopyAdditionalSettingsFrom. Across different types (a fence onto Layout Launcher, say)
    /// only the Base settings above apply.</summary>
    internal void CopySettingsFrom(LayeredWidgetForm source)
    {
        Style.TintColor = source.Style.TintColor;
        Style.TintIsExact = source.Style.TintIsExact;
        Style.HeaderDarkness = source.Style.HeaderDarkness;
        Style.Opacity = source.Style.Opacity;
        Style.FullOpacityOnHover = source.Style.FullOpacityOnHover;
        Style.TintStrength = source.Style.TintStrength;
        Style.Margin = source.Style.Margin;
        Style.CornerRadius = source.Style.CornerRadius;
        Style.TitleFontSize = source.Style.TitleFontSize;
        Style.TitleAlignment = source.Style.TitleAlignment;
        Style.HeaderBorderMode = source.Style.HeaderBorderMode;
        Style.LightBorder = source.Style.LightBorder;
        HideHeader = source.HideHeader;
        ShowHeaderCloseButton = source.ShowHeaderCloseButton;

        if (GetType() == source.GetType())
            CopyAdditionalSettingsFrom(source);

        PersistStyle();
        RenderOpacity.SnapToTarget();
        RenderAndPresent();
    }

    /// <summary>A subclass's own per-instance "Additional" settings (see BuildAdditionalSettingsRows)
    /// worth carrying over during a same-type Copy Settings To - only ever called once
    /// CopySettingsFrom has already confirmed source is the exact same concrete type as this widget,
    /// so an override can safely access source's own private fields directly (C# privacy is
    /// per-type, not per-instance) without an `is` cast of its own. No-op by default - Widget
    /// Manager's own Additional flyout (Start with Windows/Show Hidden Files) is system state, not
    /// per-instance model data, so it has nothing to copy and doesn't override this.</summary>
    protected virtual void CopyAdditionalSettingsFrom(LayeredWidgetForm source)
    {
    }

    /// <summary>Rows specific to a subclass's own feature - listed directly below "Base" in the
    /// top-level dropdown now (see BuildSettingsRows), not nested in their own "Additional" flyout
    /// the way they used to be. Still kept as their own distinct method rather than folded into
    /// BuildBaseSettingsRows, deliberately - BuildSettingsRows is the ONLY place that reads this, so
    /// putting the "Additional" flyout back later (for a subclass that ends up with enough of its own
    /// settings that a flat inline list stops reading well - a lot of unique settings, say) is a
    /// one-line change there (wrap the AddRange back into a Submenu row) rather than a rewrite here.
    /// A subclass with nothing of its own (Layout Launcher, so far) still contributes nothing extra
    /// either way. Null or empty means nothing to add. Command ids used here are the subclass's own
    /// (positive, in FenceForm's case) - route them in HandleSettingsCommand the same way, falling
    /// through to base.HandleSettingsCommand for anything not recognized (the shared ids these
    /// additional rows didn't add).</summary>
    protected virtual IReadOnlyList<DropdownMenu.Row>? BuildAdditionalSettingsRows() => null;

    /// <summary>Whether the "Light Border" row below is offered at all - true by default. A
    /// subclass whose header isn't a plain rounded band (FolderFenceForm's own folder tab, say) can
    /// turn this off if a lone stroke around just the header reads as wrong/incomplete against its
    /// own shape, rather than that combination needing to be supported everywhere.</summary>
    protected virtual bool ShowLightBorderOption => true;

    /// <summary>Font Size (a stepper, same interface as Margin/Corner Radius), Align (Left/Center/
    /// Right, see DropdownMenu.Row.IsAlignmentPicker), and Light Border - all affect the title row
    /// specifically (see TitleFont/PaintChrome's title draw and its own header-outline stroke),
    /// nested in their own "Header" flyout at the top of "Base" rather than inline, so they read as
    /// a distinct group from the fill/tint-driven rows below them.</summary>
    private List<DropdownMenu.Row> BuildHeaderSettingsRows()
    {
        var rows = new List<DropdownMenu.Row>
        {
            new(0, "Font Size", IsHeader: true),
            new(0, string.Empty, IsStepper: true,
                StepperValue: () => Style.TitleFontSize,
                OnStepperChange: size =>
                {
                    Style.TitleFontSize = Math.Clamp(size, 7, 14);
                    PersistStyle();
                    RenderAndPresent();
                },
                // Max of 14, not some rounder-looking number like 20 - TitleRowHeight is a fixed
                // ~26-28px, and a much larger point size than this renders taller than that
                // (vertically clipped by the row itself) rather than actually fitting.
                StepperMin: 7, StepperMax: 14, StepperStep: 1, StepperSuffix: ""),
            new(0, string.Empty, IsSeparator: true),
            new(0, "Align", IsHeader: true),
            new(0, string.Empty, IsAlignmentPicker: true,
                AlignmentValue: () => Style.TitleAlignment,
                OnAlignmentChange: alignment =>
                {
                    Style.TitleAlignment = alignment;
                    PersistStyle();
                    RenderAndPresent();
                }),
        };

        if (ShowLightBorderOption)
        {
            rows.Add(new(0, string.Empty, IsSeparator: true));
            // Independent of Header Border Mode (see CmdToggleHeaderBorderMode's own handling) -
            // turning Header Border Mode on ticks this OFF, since Header Border Mode already
            // borders the title row its own way, but it stays a genuinely separate flag the user
            // can tick back on afterward without touching Header Border Mode itself. Outlines just
            // the title row (see PaintChrome's own header-border stroke) in the plain ThemedBorder
            // color, unlike Header Border Mode's own themed border, which covers the rest of the
            // widget instead.
            rows.Add(new(CmdToggleLightBorder, "Light Border", HasCheckbox: true,
                IsChecked: () => Style.LightBorder,
                Tooltip: "Border the title row on its own, in the plain default color, independent of Header Border Mode"));
        }

        rows.Add(new(0, string.Empty, IsSeparator: true));
        // Unlike every other row in this flyout, paints/hit-tests regardless of ShowsButtons (see
        // GetHeaderCloseButtonRect/PaintChrome's own close-glyph draw) - the whole point is a close
        // action reachable without first engaging the widget the way Settings/Extra buttons require.
        rows.Add(new(CmdToggleHeaderCloseButton, "Close Button", HasCheckbox: true,
            IsChecked: () => ShowHeaderCloseButton,
            Tooltip: "Always show a close button in the title row, without needing to right-click or click the title first"));

        return rows;
    }

    /// <summary>Floor for Opacity (see StyleMenuRows.Build's own slider, which otherwise allows the
    /// full 0-100%) - 0% would be both fully invisible and (per LayeredWindowPresenter's own doc
    /// comment) click-through, with no way to get it back short of editing the persisted JSON by
    /// hand. Inherent to any WS_EX_LAYERED window driven by RenderOpacity, not a Fence-specific safety
    /// margin, so it's enforced once here rather than separately by each subclass's own SetOpacity
    /// (or, worse, by whatever persists it - see FenceManager/LayoutLauncherWidget's own now-removed
    /// copies of this exact clamp, which had drifted to two different floors, 15 and 5).</summary>
    protected const int MinOpacity = 15;

    /// <summary>Whether the "Hide Header" row below is offered at all - true by default. A subclass
    /// whose header is load-bearing for something beyond just showing a title (FolderFenceForm's own
    /// folder tab, which has nothing to attach to without a header underneath it) can turn this off
    /// rather than that combination needing to be supported everywhere.</summary>
    protected virtual bool ShowHideHeaderOption => true;

    /// <summary>Ceiling for the Corner Radius stepper below - 50 by default. A subclass whose own
    /// shape doesn't hold up as well at large radii (FolderFenceForm's own folder-tab geometry,
    /// whose tab/diagonal proportions are sized for a more modest range) can lower this instead of
    /// that combination needing to be supported everywhere.</summary>
    protected virtual int CornerRadiusMax => 50;

    /// <summary>The "Header" flyout (see BuildHeaderSettingsRows), then Hide Header, Full Opacity When
    /// Active, and the shared color grid/Header Darkness/Opacity/Tint Strength sliders/Corner Radius/
    /// Margin steppers (StyleMenuRows.Build) - every setting LayeredWidgetForm itself owns, regardless
    /// of subclass. Shown in its own "Base" flyout (see BuildSettingsRows) rather than inline, so it
    /// reads as a distinct group from whatever a subclass's own BuildAdditionalSettingsRows
    /// contributes below it.
    ///
    /// Each row mutates Style directly and clamps right here (DropdownMenu's own stepper/slider
    /// mechanics already guarantee an in-range value for every row here except Opacity, whose slider
    /// allows the full 0-100% with no built-in floor of its own - clamping the rest too is just
    /// documentation at this point, not load-bearing) - a subclass's own PersistStyle never has to
    /// know or re-validate which property changed.</summary>
    private List<DropdownMenu.Row> BuildBaseSettingsRows()
    {
        var rows = new List<DropdownMenu.Row>
        {
            new(0, "Header", Submenu: BuildHeaderSettingsRows()),
            new(0, string.Empty, IsSeparator: true),
        };
        if (ShowHideHeaderOption)
            rows.Add(new(CmdToggleHideHeader, "Hide Header", HasCheckbox: true, IsChecked: () => HideHeader));
        rows.Add(new(CmdToggleFullOpacityOnHover, "Full Opacity When Active", HasCheckbox: true,
            IsChecked: () => Style.FullOpacityOnHover,
            Tooltip: "Full opacity while hovered, dragged/resized, or this menu is open"));
        rows.Add(new(CmdToggleHeaderBorderMode, "Header Border Mode", HasCheckbox: true,
            IsChecked: () => Style.HeaderBorderMode,
            Tooltip: "Border every element (the widget itself, its buttons, its list) in the header's own color"));
        rows.Add(new(0, string.Empty, IsSeparator: true));
        rows.AddRange(StyleMenuRows.Build(Style, DefaultBodyColor, CmdColorDefault, CmdColorCustom, CmdColorEyedrop, CmdColorPresetBase,
            darkness =>
            {
                Style.HeaderDarkness = Math.Clamp(darkness, 0, 100);
                PersistStyle();
                RenderAndPresent();
            },
            opacity =>
            {
                // Snaps RenderOpacity straight to the new TargetOpacity instead of easing - a slider
                // drag needs to track the cursor immediately, an animated lag here would feel
                // unresponsive.
                Style.Opacity = Math.Clamp(opacity, MinOpacity, 100);
                PersistStyle();
                RenderOpacity.SnapToTarget();
                RenderAndPresent();
            },
            strength =>
            {
                Style.TintStrength = Math.Clamp(strength, 0, 100);
                PersistStyle();
                RenderAndPresent();
            },
            radius =>
            {
                Style.CornerRadius = Math.Clamp(radius, 0, CornerRadiusMax);
                PersistStyle();
                RenderAndPresent();
            },
            margin =>
            {
                Style.Margin = Math.Clamp(margin, 0, 100);
                PersistStyle();
                RenderAndPresent();
            },
            cornerRadiusMax: CornerRadiusMax));
        return rows;
    }

    /// <summary>The Settings dropdown's default row list - whichever ExtraButtons don't currently fit
    /// on the bar (see BuildOverflowButtonRows/VisibleExtraButtonCount), then "Base" (see
    /// BuildBaseSettingsRows, always present, still its own flyout), then a subclass's own additional
    /// rows (see BuildAdditionalSettingsRows) listed directly inline rather than tucked into a second
    /// "Additional" flyout the way they used to be - a separator marks where the relocated button row
    /// ends and Base's own opener row begins, and another marks where Base ends and a subclass's own
    /// rows begin, since there's no flyout boundary doing either job anymore. BuildAdditionalSettingsRows
    /// is still its own distinct method (not folded into BuildBaseSettingsRows) specifically so this
    /// is a one-line revert (wrap the AddRange below back into a Submenu row) if a subclass with a lot
    /// of unique settings ever wants its own flyout back. Virtual, not sealed, in case a subclass ever
    /// needs a genuinely different shape rather than just extra rows - but BuildAdditionalSettingsRows
    /// should cover that need first.</summary>
    protected virtual List<DropdownMenu.Row> BuildSettingsRows()
    {
        var rows = new List<DropdownMenu.Row>();
        var overflowButtons = BuildOverflowButtonRows().ToList();
        rows.AddRange(overflowButtons);
        if (overflowButtons.Count > 0)
            rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        rows.Add(new(0, "Base", Submenu: BuildBaseSettingsRows()));
        if (BuildAdditionalSettingsRows() is { Count: > 0 } additional)
        {
            rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
            rows.AddRange(additional);
        }
        return rows;
    }

    /// <summary>One DropdownMenu.Row(IsButtonRow: true) for each AllBarButtons entry (Copy Settings
    /// included) that doesn't currently fit on the bar (see VisibleExtraButtonCount) - empty once
    /// every button fits. Same icon each one uses on the bar (custom PaintGlyph, or a plain centered
    /// Label when none was given, matching PaintExtraButtons' own rendering), just relocated to the
    /// top of the Settings dropdown instead (see BuildSettingsRows). Each button's own OnClick fires
    /// directly (see Row.ButtonOnClick) rather than through a synthetic command id - ChromeButton.
    /// OnClick is already a plain Action, so there's nothing for HandleSettingsCommand to dispatch
    /// here.</summary>
    private IEnumerable<DropdownMenu.Row> BuildOverflowButtonRows() =>
        AllBarButtons.Skip(VisibleExtraButtonCount(GetContentSize().Width))
            .Select(button => new DropdownMenu.Row(0, string.Empty, IsButtonRow: true,
                ButtonGlyph: button.PaintGlyph ?? ((g, rect) => DrawDefaultButtonGlyph(g, rect, button.Label)),
                ButtonOnClick: button.OnClick, Tooltip: button.EffectiveTooltip));

    /// <summary>The same plain-centered-text rendering PaintExtraButtons itself falls back to for a
    /// ChromeButton with no custom PaintGlyph - shared so a relocated button reads identically
    /// wherever it currently is.</summary>
    private void DrawDefaultButtonGlyph(Graphics g, Rectangle rect, string label)
    {
        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(Color.WhiteSmoke))
        using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(label, Font, textBrush, rect, textFormat);
        g.TextRenderingHint = previousTextHint;
    }

    /// <summary>Dispatches BuildSettingsRows' own default row ids - a subclass with its own additional
    /// rows (see BuildAdditionalSettingsRows) handles its own command ids first and falls through to
    /// base.HandleSettingsCommand(id) for anything else, rather than overriding this entirely.</summary>
    protected virtual void HandleSettingsCommand(int id)
    {
        switch (id)
        {
            case CmdToggleHideHeader:
                HideHeader = !HideHeader;
                RenderAndPresent();
                break;
            case CmdToggleFullOpacityOnHover:
                Style.FullOpacityOnHover = !Style.FullOpacityOnHover;
                PersistStyle();
                RenderOpacity.SnapToTarget();
                RenderAndPresent();
                break;
            case CmdToggleHeaderBorderMode:
                Style.HeaderBorderMode = !Style.HeaderBorderMode;
                // Turning Header Border Mode on ticks Light Border OFF - Header Border Mode already
                // borders the title row its own way, so the two would otherwise double up - see
                // CmdToggleLightBorder's own doc comment on BuildHeaderSettingsRows for why this is
                // one-directional (turning Header Border Mode back off doesn't force it back on).
                if (Style.HeaderBorderMode)
                    Style.LightBorder = false;
                PersistStyle();
                RenderAndPresent();
                break;
            case CmdToggleLightBorder:
                Style.LightBorder = !Style.LightBorder;
                PersistStyle();
                RenderAndPresent();
                break;
            case CmdToggleHeaderCloseButton:
                ShowHeaderCloseButton = !ShowHeaderCloseButton;
                RenderAndPresent();
                break;
            case CmdColorDefault:
            case CmdColorCustom:
            case CmdColorEyedrop:
            case >= CmdColorPresetBase and < CmdColorPresetBase + 100:
                StyleMenuRows.TryHandleColorCommand(id, CmdColorDefault, CmdColorCustom, CmdColorEyedrop, CmdColorPresetBase,
                    DefaultBodyColor, this, CurrentTint,
                    color => ApplyTintPick(color, exact: false),
                    color => ApplyTintPick(color, exact: true));
                break;
        }
    }

    /// <summary>Body/title fill, border (engagement-aware - see ShowsButtons/ThemedActiveBorder),
    /// title text, and the Settings button - the chrome every widget on this base shares. A
    /// subclass's own PaintContent calls this first, then draws whatever's unique to it (a Fence's
    /// item grid and its own extra buttons chained off the Settings button - see
    /// GetSettingsButtonRect) on top. Corner rounding comes from Style.CornerRadius (see
    /// IWidgetStyle) rather than a parameter, now that it's a user-adjustable per-element setting
    /// same as Margin, not a fixed per-subclass constant.</summary>
    protected void PaintChrome(Graphics g, int contentWidth, int contentHeight)
    {
        var cornerRadius = Style.CornerRadius;
        using var body = GetBodyOutlinePath(contentWidth, contentHeight, cornerRadius);

        if (TitleVisible)
        {
            // Filled as two non-overlapping regions rather than the whole rounded body (`body`,
            // reused below only for the border stroke) followed by the header's own own fill shape
            // painted again directly on top of it - see RoundedRectPath.BottomFilled's own doc
            // comment for why that used to leave a faint seam along the header's left/right edges.
            using (var bodyBelowHeader = RoundedRectPath.BottomFilled(ToWindow(new Rectangle(0, 0, contentWidth - 1, contentHeight - 1)), cornerRadius, TitleRowHeight))
            using (var bodyFill = new SolidBrush(ThemedBody))
                g.FillPath(bodyFill, bodyBelowHeader);

            using var titleFill = new SolidBrush(ThemedTitle);
            using var titlePath = GetHeaderFillPath(contentWidth, cornerRadius);
            g.FillPath(titleFill, titlePath);
        }
        else
        {
            using var bodyFill = new SolidBrush(ThemedBody);
            g.FillPath(bodyFill, body);
        }

        // ShowsButtons (right-click activation) always wins over Header Border Mode here - the bright
        // ThemedActiveBorder is how an activated widget reads at all, so Header Border Mode only
        // replaces the plain inactive-state ThemedBorder, never this.
        var borderColor = ShowsButtons ? ThemedActiveBorder : (Style.HeaderBorderMode ? ThemedTitle : ThemedBorder);
        using (var borderPen = new Pen(borderColor, BorderWidth))
        {
            borderPen.LineJoin = LineJoin.Round;
            // Inset, not the default Center - a centered stroke needs half its own width to bleed
            // outward into the margin band beyond the body's edge, but TopBand is deliberately 0 on
            // whichever side currently holds the flipped button row (see OuterMargin's own doc
            // comment on why), leaving no margin pixels there for that outward half to render into -
            // it was getting clipped by the window's own bitmap bounds. Inset keeps the whole stroke
            // within the body itself, which needs no such margin on any edge.
            //
            // Always wraps the whole body (`body`, the full rounded rect), header included - a
            // previous version stopped this stroke short of the header in the plain, unengaged,
            // Header-Border-Mode-off state (RoundedRectPath.Bottom), leaving the header with no
            // border of its own there at all. That read as a genuine misalignment, not a deliberate
            // "cleaner" look: with only the body stroked, its edge sits this pen's own half-width
            // inset from the fill, while the header's un-stroked edge is exactly at the fill's own
            // (antialiased) boundary - a visible seam right where header meets body, on both the
            // left and right sides. Wrapping the header too keeps both edges defined the same way.
            g.DrawPath(borderPen, body);
        }

        // Light Border - see IWidgetStyle.LightBorder's own doc comment - a separate stroke around
        // just the title row, independent of the outer body border above (Header Border Mode
        // included): drawn in ThemedBorder rather than ThemedTitle, since a ThemedTitle stroke would
        // blend invisibly into the title band's own ThemedTitle fill sitting right underneath it.
        // Always drawn when on, regardless of ShowsButtons - unlike the outer border, this isn't
        // trying to double as an "engaged" indicator, just a persistent on/off accent. Also gated on
        // ShowLightBorderOption, not just Style.LightBorder itself - a subclass that turned the
        // setting off (FolderFenceForm, whose header-only stroke never accounts for its own folder
        // tab) needs this to actually stop rendering too, not just lose the menu row that used to
        // toggle it - otherwise a fence that already had it on from before keeps showing it forever
        // with no way left to turn it back off.
        if (TitleVisible && Style.LightBorder && ShowLightBorderOption)
        {
            using var titleBorderPen = new Pen(ThemedBorder, 1f) { LineJoin = LineJoin.Round, Alignment = PenAlignment.Inset };
            using var titleBorderPath = GetHeaderFillPath(contentWidth, cornerRadius);
            g.DrawPath(titleBorderPen, titleBorderPath);
        }

        if (TitleVisible && !IsRenaming)
        {
            var alignment = Style.TitleAlignment switch
            {
                TitleAlignment.Center => StringAlignment.Center,
                TitleAlignment.Right => StringAlignment.Far,
                _ => StringAlignment.Near,
            };

            // GDI+'s DrawString instead of GDI's TextRenderer.DrawText - same reasoning as
            // PaintSettingsButton's own comment just below (ClearType fringing against a saturated
            // background), plus DrawString respects Graphics.Transform, which TextRenderer.DrawText
            // does not - see RenderAndPresent's own supersampling comment.
            var previousTextHint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var textBrush = new SolidBrush(Color.WhiteSmoke))
            using (var textFormat = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(DisplayTitle, TitleFont, textBrush, ToWindow(new Rectangle(8 + TitleTextInset, 0, TitleRowAvailableWidth(contentWidth), TitleRowHeight)), textFormat);
            g.TextRenderingHint = previousTextHint;
        }

        // Unlike the title text above, paints regardless of IsRenaming - the rename edit box already
        // stops short of it (see BeginRename), so there's no overlap to avoid, and no reason to make
        // closing unreachable just because a rename happens to be in progress.
        if (TitleVisible && ShowHeaderCloseButton)
        {
            var closeButtonRect = ToWindow(GetHeaderCloseButtonRect(contentWidth));
            if (_hoveredButtonKind == HoveredButtonKind.HeaderClose)
                PaintButtonHoverTint(g, closeButtonRect);

            var previousTextHint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var textBrush = new SolidBrush(Color.WhiteSmoke))
            using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("×", Font, textBrush, closeButtonRect, textFormat);
            g.TextRenderingHint = previousTextHint;
        }

        if (ShowsButtons)
        {
            PaintSettingsButton(g, contentWidth);
            // Copy Settings is AllBarButtons[0] now - PaintExtraButtons paints it right along with
            // every ExtraButtons entry, so there's no separate call for it here anymore.
            PaintExtraButtons(g, contentWidth);
        }

        PaintList(g, contentWidth, contentHeight);
        PaintContentButtons(g, contentWidth, contentHeight);

        // Last, so it sits on top of everything else this method just painted - same reasoning as
        // every other row/button tooltip in this app.
        _chromeButtonTooltip.Paint(g, Font, SettingsMenuTooltipColor, ToWindow(new Rectangle(0, 0, contentWidth, contentHeight)),
            Style.HeaderBorderMode ? ThemedTitle : null);
    }

    private void PaintSettingsButton(Graphics g, int contentWidth)
    {
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        var buttonRect = ToWindow(GetSettingsButtonRect(contentWidth, onLeft));

        // Filled first so the button reads as fully opaque - it lives in the near-transparent margin
        // band (see MarginFillColor), and TextRenderer.DrawText/DrawString below only ever writes
        // RGB, never alpha, so without an opaque backing shape under it the label would inherit the
        // margin's near-zero alpha and vanish once RenderAndPresent's own alpha-scaling runs.
        using (var buttonPath = RoundedRectPath.Full(buttonRect, 6))
        {
            using (var buttonFill = new SolidBrush(ThemedField))
                g.FillPath(buttonFill, buttonPath);
            PaintHeaderBorderModeOutline(g, buttonPath);
        }

        if (_hoveredButtonKind == HoveredButtonKind.Settings)
            PaintButtonHoverTint(g, buttonRect);

        // GDI+'s DrawString instead of the GDI TextRenderer.DrawText used for the title above - GDI's
        // own ClearType antialiasing assumes a neutral/opaque background and fringes with visible
        // red/blue "shadow" pixels along each glyph's edge against a saturated color like ChromeFill
        // can be; GDI+'s AntiAlias hint is plain grayscale, so it doesn't.
        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(Color.WhiteSmoke))
        using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString("Settings", Font, textBrush, buttonRect, textFormat);
        g.TextRenderingHint = previousTextHint;
    }

    /// <summary>The classic two-overlapping-squares "duplicate" glyph, same hand-drawn approach as
    /// LayoutLauncherWidget's own row-level PaintCopyGlyph (no icon asset library in this app - see
    /// WarningIcon's own comment). The front square's corner is punched out of the back square first
    /// using ThemedField (whichever bar button this is currently painting for - see PaintExtraButtons
    /// - own fill) so it reads as sitting on top instead of two crossing outlines. Protected, not
    /// private - FenceForm's own Copy Fence button uses this too (see its own ExtraButtons), not just
    /// this base's Copy Settings button that originally motivated it.</summary>
    protected void PaintCopyIconGlyph(Graphics g, Rectangle rect)
    {
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        var scale = Math.Min(rect.Width, rect.Height);
        var iconSize = scale * 0.42f;
        var iconOffset = scale * 0.16f;

        var previousSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var backRect = new RectangleF(cx - iconSize / 2f + iconOffset / 2f, cy - iconSize / 2f - iconOffset / 2f, iconSize, iconSize);
        var frontRect = new RectangleF(cx - iconSize / 2f - iconOffset / 2f, cy - iconSize / 2f + iconOffset / 2f, iconSize, iconSize);

        using (var copyPen = new Pen(Color.WhiteSmoke, 1.1f))
        {
            g.DrawRectangle(copyPen, backRect.X, backRect.Y, backRect.Width, backRect.Height);
            using (var punchBrush = new SolidBrush(ThemedField))
                g.FillRectangle(punchBrush, frontRect);
            g.DrawRectangle(copyPen, frontRect.X, frontRect.Y, frontRect.Width, frontRect.Height);
        }

        g.SmoothingMode = previousSmoothing;
    }

    /// <summary>"Copy Settings To" - a simplified eyedropper/pipette (a diagonal shaft with a filled
    /// tip, the same "drop" a real one leaves), proportioned off whichever rect it's handed rather
    /// than the fixed pixel offsets DropdownMenu's own GridGlyph.Eyedropper uses for its always-20px
    /// color-grid circle. Icon-only, no text label - the button is too small for "Copy Settings To"
    /// to fit, hence the tooltip (see _chromeButtonTooltip).</summary>
    private void PaintEyedropperGlyph(Graphics g, Rectangle rect)
    {
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        var scale = Math.Min(rect.Width, rect.Height);
        var half = scale * 0.26f;
        var tipRadius = Math.Max(1.2f, scale * 0.09f);

        var previousSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var x1 = cx + half;
        var y1 = cy - half;
        var x2 = cx - half * 0.7f;
        var y2 = cy + half * 0.7f;
        using (var dropperPen = new Pen(Color.WhiteSmoke, Math.Max(1f, scale * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(dropperPen, x1, y1, x2, y2);
        using (var tipBrush = new SolidBrush(Color.WhiteSmoke))
            g.FillEllipse(tipBrush, x2 - tipRadius, y2 - tipRadius, tipRadius * 2, tipRadius * 2);

        g.SmoothingMode = previousSmoothing;
    }

    /// <summary>The same translucent-white tint a Fence's own icon-grid hover uses (see FenceForm.
    /// PaintContent's _hoverIndex check), shared by Settings/ChromeButton/ContentButton so all three
    /// read as one consistent hover language across every LayeredWidgetForm - protected, not private,
    /// so a subclass's own hand-painted list rows (CopySettingsGroupPicker's own boxes) can reuse the
    /// exact same tint instead of a second copy of it.</summary>
    protected static void PaintButtonHoverTint(Graphics g, Rectangle windowRect)
    {
        using var hoverBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
        using var hoverPath = RoundedRectPath.Full(windowRect, 6);
        g.FillPath(hoverBrush, hoverPath);
    }

    /// <summary>Header Border Mode's own outline (see IWidgetStyle.HeaderBorderMode) - a no-op unless
    /// it's on, shared by Settings/ChromeButton/ContentButton/PaintList so every element borders
    /// itself the same way, and draws NO border at all otherwise (not a fixed default color swapped
    /// for a themed one - these elements are borderless by default, full stop, same as Settings/
    /// ChromeButton/ContentButton already are). Protected so a subclass's own hand-drawn buttons that
    /// aren't ChromeButtons (a Fence's own New/Delete squares - see AdditionalFullOpacityRegions' own
    /// doc comment on why those stay separate) can opt into the exact same all-or-nothing behavior
    /// instead of always drawing some border.
    ///
    /// Same ShowsButtons-wins-over-HeaderBorderMode color choice as PaintChrome's own outer body
    /// border - Settings/ChromeButton/ContentButton are only ever painted while ShowsButtons is true
    /// in the first place (see PaintChrome's own `if (ShowsButtons)` guard), which is also exactly
    /// when the outer body border switches to ThemedActiveBorder - without matching that here too,
    /// these elements' own ThemedTitle outline and the body's own ThemedActiveBorder outline would
    /// visibly disagree on color the whole time either one is even visible to compare against the
    /// other (i.e. always, in practice, since a button only shows up alongside an active-colored
    /// body edge).</summary>
    protected void PaintHeaderBorderModeOutline(Graphics g, GraphicsPath path)
    {
        if (!Style.HeaderBorderMode)
            return;
        using var borderPen = new Pen(ShowsButtons ? ThemedActiveBorder : ThemedTitle, 1f);
        g.DrawPath(borderPen, path);
    }

    /// <summary>A button chained off the Settings button - same rounded-rect chrome as Settings
    /// itself, painted either as a short centered text label (Layout Launcher's Close, Widget
    /// Manager's Help) or, when PaintGlyph is supplied, a custom hand-drawn icon (a Fence's own
    /// Copy/Delete squares) instead of hand-rolling the same rect-chaining/paint/hit-test/arm-fire
    /// plumbing per button. Once this bar band gets too narrow to fit every declared button (see
    /// VisibleExtraButtonCount), buttons drop off the bar outermost-first, one at a time as space
    /// runs out - not all-or-nothing - and reappear as their own small icons at the top of the
    /// Settings dropdown instead (see BuildSettingsRows), live-tracking width both directions. Tooltip
    /// is what that dropdown row (and this button's own bar hover tooltip - see UpdateButtonHover)
    /// shows for it, since PaintGlyph draws no text of its own to fall back on. Falls back to Label
    /// itself when Tooltip is left null, for a button whose glyph already doubles as an adequate name
    /// (a plain "×", say).</summary>
    protected readonly record struct ChromeButton(string Label, int Width, Action OnClick,
        string? Tooltip = null, Action<Graphics, Rectangle>? PaintGlyph = null)
    {
        internal string EffectiveTooltip => Tooltip ?? Label;
    }

    /// <summary>Extra buttons chained immediately outward from Copy Settings, in declared order -
    /// none by default, only shown/hit-testable while ShowsButtons is true (same as Settings itself).
    /// See AllBarButtons for where Copy Settings itself fits into this same chain.</summary>
    protected virtual IReadOnlyList<ChromeButton> ExtraButtons => Array.Empty<ChromeButton>();

    /// <summary>"Copy Settings To" as a ChromeButton, folded into the same bar-button chain as
    /// ExtraButtons (see AllBarButtons) instead of being its own separate, always-visible-regardless-
    /// of-width mechanism the way it used to be - Width/OnClick mirror exactly what the old dedicated
    /// TryArmCopySettingsButton/FireArmedCopySettingsButton hard-coded. OpenCopySettingsPicker is the
    /// same picker-opening logic FireArmedCopySettingsButton used to run inline, just extracted so it
    /// can be this button's own OnClick. PaintEyedropperGlyph (a "sample this widget's look" pipette)
    /// rather than the two-squares "duplicate" glyph - that one now belongs to FenceForm's own Copy
    /// Fence button instead (see its own ExtraButtons), which is a much more literal duplicate.</summary>
    private ChromeButton CopySettingsButton => new(
        "Copy Settings To", CopySettingsButtonWidth, OpenCopySettingsPicker, "Copy Settings To", PaintEyedropperGlyph);

    /// <summary>The full bar-button chain, in order: Copy Settings, then every subclass-declared
    /// ExtraButtons entry. GetExtraButtonRect/VisibleExtraButtonCount/PaintExtraButtons/
    /// TryGetExtraButtonAt/BuildOverflowButtonRows all operate over this single combined list now -
    /// Copy Settings used to be a wholly separate mechanism with no overflow handling of its own,
    /// which meant it stayed pinned to the bar even once there was visibly no room for it; folding it
    /// in here means it gets exactly the same fluid, one-at-a-time drop-to-the-dropdown treatment
    /// every other bar button already has (see VisibleExtraButtonCount).</summary>
    private IReadOnlyList<ChromeButton> AllBarButtons
    {
        get
        {
            var combined = new List<ChromeButton>(ExtraButtons.Count + 1) { CopySettingsButton };
            combined.AddRange(ExtraButtons);
            return combined;
        }
    }

    /// <summary>How many of AllBarButtons (Copy Settings first, then every ExtraButtons entry in
    /// order), counting from index 0, still fit on the bar at contentWidth - generalizes what used to
    /// be FenceForm's own private, hand-rolled ButtonBandIsTight (an all-or-nothing check sized for
    /// exactly its own two buttons) to this whole chain, of any length, and fluidly rather than as a
    /// single breakpoint: as contentWidth shrinks, buttons drop off the bar outermost-first, one at a
    /// time, exactly as each one's own chained position would first slide past the window's edge -
    /// not everything at once just because the last one no longer fits. Whatever doesn't fit
    /// (AllBarButtons.Count minus this) reappears as icons at the top of the Settings dropdown
    /// instead (see BuildOverflowButtonRows) - same total buttons, same reach, split live between the
    /// two places, both directions, purely as a function of the current width.</summary>
    protected int VisibleExtraButtonCount(int contentWidth)
    {
        var total = SettingsButtonWidth;
        var count = 0;
        foreach (var button in AllBarButtons)
        {
            var next = total + SettingsButtonGap + button.Width;
            if (next > contentWidth)
                break;
            total = next;
            count++;
        }
        return count;
    }

    // Armed on a subclass's own OnMouseDown (see TryArmExtraButton), fired on the matching OnMouseUp
    // only if the cursor is still over the same button (see FireArmedExtraButton) - the same
    // arm-then-fire pattern FenceForm's own Settings/New/Delete buttons already use, just centralized
    // here instead of each subclass keeping its own "which button is currently armed" field. Covers
    // Copy Settings too now (index 0 of AllBarButtons) - there's no separate armed flag for it anymore.
    private int? _armedExtraButtonIndex;

    /// <summary>Chains outward from the Settings button itself - index 0 (Copy Settings) sits
    /// immediately next to it, index 1 next to that, and so on through AllBarButtons. Each button
    /// uses its own declared Width (see ChromeButton) rather than a fixed size, so a short Close
    /// glyph and a much wider "Manage Layouts..." label can both chain correctly.</summary>
    protected Rectangle GetExtraButtonRect(int contentWidth, bool onLeft, int index)
    {
        var buttons = AllBarButtons;
        var previous = GetSettingsButtonRect(contentWidth, onLeft);
        var current = previous;
        for (var i = 0; i <= index; i++)
        {
            var width = buttons[i].Width;
            var x = onLeft ? previous.Right + SettingsButtonGap : previous.X - SettingsButtonGap - width;
            current = new Rectangle(x, previous.Y, width, SettingsButtonHeight);
            previous = current;
        }
        return current;
    }

    /// <summary>Which bar button (Copy Settings or an ExtraButtons entry - see AllBarButtons), if
    /// any, contentPoint lands on - used both for a subclass's own HitTest (to route a click there to
    /// HTCLIENT instead of the margin's move/resize handling) and internally by TryArmExtraButton.
    /// Only ever matches a currently-visible button (see VisibleExtraButtonCount) - anything beyond
    /// that isn't painted on the bar (see PaintExtraButtons), so nothing here should be clickable
    /// there either.</summary>
    protected bool TryGetExtraButtonAt(int contentWidth, bool onLeft, Point contentPoint, out int index)
    {
        index = -1;
        var visibleCount = VisibleExtraButtonCount(contentWidth);
        for (var i = 0; i < visibleCount; i++)
        {
            if (GetExtraButtonRect(contentWidth, onLeft, i).Contains(contentPoint))
            {
                index = i;
                return true;
            }
        }
        return false;
    }

    /// <summary>The picker-opening half of what a click on the Copy Settings button does (see
    /// CopySettingsButton) - opens the cross-widget picker overlay (see CopySettingsOverlay), with
    /// this widget as the copy source. Used as that button's own OnClick, fired the same way any
    /// other AllBarButtons entry's OnClick is (see FireArmedExtraButton) - no separate armed flag or
    /// "still hovering" re-check needed here anymore, FireArmedExtraButton already does both
    /// generically before invoking OnClick at all.</summary>
    private void OpenCopySettingsPicker()
    {
        var overlay = new CopySettingsOverlay(this);
        var pickerStore = new CopySettingsPickerStore();
        var groupPicker = new CopySettingsGroupPicker(this, Fences, pickerStore.Load(), pickerStore);
        // Seeds the picker's own look from this widget's current one, the same copy every individual
        // target gets - reuses CopySettingsFrom itself rather than duplicating its dozen-property
        // list here. Applied on top of the picker's own persisted position/size (already set by its
        // constructor), not instead of them - only the tint/opacity/etc half of CopySettingsFrom's
        // work is meant to track the source; where the picker itself last sat on screen is its own.
        groupPicker.CopySettingsFrom(this);

        // Forced to full opacity regardless of the source's own current Opacity - a picker that
        // copied a faded-into-the-desktop fence's low Opacity would be hard to read/click reliably
        // right when it needs to be clearest. Still just Style.Opacity underneath, so the picker's
        // own Settings menu can lower it again afterward like any other widget - only the value this
        // particular pick starts at is forced, not the knob itself.
        groupPicker.Style.Opacity = 100;
        groupPicker.RenderOpacity.SnapToTarget();
        groupPicker.PersistStyle();
        groupPicker.RenderAndPresent();

        // The two halves of one pick (see CopySettingsGroupPicker's own class comment) - a cancel
        // landing on either one closes both, guarded so the mutual FormClosed handlers below don't
        // just bounce a second Close() back and forth once the first has already fired.
        var closingBoth = false;
        overlay.FormClosed += (_, _) =>
        {
            if (!closingBoth) { closingBoth = true; groupPicker.Close(); }
            overlay.Dispose();
        };
        groupPicker.FormClosed += (_, _) =>
        {
            if (!closingBoth) { closingBoth = true; overlay.Close(); }
            groupPicker.Dispose();
        };

        overlay.Show();
        groupPicker.Show();
    }

    /// <summary>Arms whichever extra button contentPoint lands on, if any - a subclass's own
    /// OnMouseDown calls this (typically as the fallback once its own Settings-button check misses),
    /// mirroring the same arm-then-fire pattern as the Settings button itself.</summary>
    protected bool TryArmExtraButton(Point contentPoint)
    {
        var contentWidth = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        if (!TryGetExtraButtonAt(contentWidth, onLeft, contentPoint, out var index))
            return false;
        _armedExtraButtonIndex = index;
        return true;
    }

    /// <summary>Fires whichever extra button was armed (see TryArmExtraButton), but only if the mouse
    /// is still over that same button on release - a subclass's own OnMouseUp calls this.</summary>
    protected void FireArmedExtraButton(Point contentPoint)
    {
        if (_armedExtraButtonIndex is not int index)
            return;
        _armedExtraButtonIndex = null;

        var buttons = AllBarButtons;
        if (index >= buttons.Count)
            return;
        var contentWidth = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        if (GetExtraButtonRect(contentWidth, onLeft, index).Contains(contentPoint))
            buttons[index].OnClick();
    }

    private void PaintExtraButtons(Graphics g, int contentWidth)
    {
        var buttons = AllBarButtons;
        // Only paints whichever buttons currently fit - whatever doesn't reappears in the Settings
        // dropdown instead (see BuildSettingsRows/VisibleExtraButtonCount's own doc comment).
        var visibleCount = VisibleExtraButtonCount(contentWidth);
        if (visibleCount == 0)
            return;

        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        for (var i = 0; i < visibleCount; i++)
        {
            var buttonRect = ToWindow(GetExtraButtonRect(contentWidth, onLeft, i));
            // Same opaque-backing/GDI+-AntiAlias reasoning as PaintSettingsButton's own two comments.
            using (var buttonPath = RoundedRectPath.Full(buttonRect, 6))
            {
                using (var buttonFill = new SolidBrush(ThemedField))
                    g.FillPath(buttonFill, buttonPath);
                PaintHeaderBorderModeOutline(g, buttonPath);
            }

            if (_hoveredButtonKind == HoveredButtonKind.Extra && _hoveredButtonIndex == i)
                PaintButtonHoverTint(g, buttonRect);

            if (buttons[i].PaintGlyph is { } paintGlyph)
            {
                paintGlyph(g, buttonRect);
                continue;
            }

            var previousTextHint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var textBrush = new SolidBrush(Color.WhiteSmoke))
            using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(buttons[i].Label, Font, textBrush, buttonRect, textFormat);
            g.TextRenderingHint = previousTextHint;
        }
    }

    /// <summary>A simple text-labeled button drawn inside the widget's own visible content area -
    /// unlike ChromeButton (chained off Settings in the margin band, only shown/hit-testable once
    /// ShowsButtons is true), this reads as part of the widget's own surface, the same way a Fence's
    /// icon grid is always visible/clickable regardless of activation state. ContentRect is
    /// content-relative (see ToWindow) - a subclass picks its own placement since, unlike the
    /// universal Settings-button chain, in-body layout is inherently subclass-specific.</summary>
    protected readonly record struct ContentButton(string Label, Rectangle ContentRect, Action OnClick);

    /// <summary>A subclass's own in-body buttons (Layout Launcher's Manage Layouts.../Save Current
    /// Layout) - none by default. Recomputed on demand from the current content size rather than
    /// cached, since a resizable widget's own layout can change contentWidth/contentHeight between
    /// calls.</summary>
    protected virtual IReadOnlyList<ContentButton> GetContentButtons(int contentWidth, int contentHeight) => Array.Empty<ContentButton>();

    /// <summary>Total height LayoutRow will occupy for the same contentWidth/height/gap/widths - a
    /// single row's height when everything fits side by side, or every item stacked with gap between
    /// when it doesn't (see LayoutRow's own doc comment for the fits-or-stacks rule itself). A caller
    /// anchoring the row's bottom rather than its top (Layout Launcher's own row, pinned to the bottom
    /// of the body) uses this to know how tall the block will actually turn out before calling
    /// LayoutRow itself.</summary>
    protected static int RowHeight(int contentWidth, int height, int gap, IReadOnlyList<int> widths) =>
        RowFits(contentWidth, gap, widths) ? height : height * widths.Count + gap * (widths.Count - 1);

    private static bool RowFits(int contentWidth, int gap, IReadOnlyList<int> widths)
    {
        var totalWidth = 0;
        for (var i = 0; i < widths.Count; i++)
            totalWidth += widths[i];
        totalWidth += gap * (widths.Count - 1);
        return totalWidth <= contentWidth;
    }

    /// <summary>The standard layout rule for a row of same-height in-body elements (see
    /// GetContentButtons): laid out left-to-right and centered as one group when they all fit within
    /// contentWidth, or each centered on its own stacked row (top-to-bottom from top) when they don't -
    /// rather than shrinking or letting them overflow. widths.Count must equal the returned array's
    /// length; each rect shares the same width its own widths entry declared.</summary>
    protected static Rectangle[] LayoutRow(int contentWidth, int top, int height, int gap, IReadOnlyList<int> widths)
    {
        var rects = new Rectangle[widths.Count];

        if (RowFits(contentWidth, gap, widths))
        {
            var totalWidth = 0;
            for (var i = 0; i < widths.Count; i++)
                totalWidth += widths[i];
            totalWidth += gap * (widths.Count - 1);
            var x = (contentWidth - totalWidth) / 2;
            for (var i = 0; i < widths.Count; i++)
            {
                rects[i] = new Rectangle(x, top, widths[i], height);
                x += widths[i] + gap;
            }
        }
        else
        {
            var y = top;
            for (var i = 0; i < widths.Count; i++)
            {
                rects[i] = new Rectangle((contentWidth - widths[i]) / 2, y, widths[i], height);
                y += height + gap;
            }
        }

        return rects;
    }

    // Same arm-then-fire pattern as _armedExtraButtonIndex, kept as a separate field since a content
    // button and an extra button could theoretically be armed independently across two mouse-downs.
    private int? _armedContentButtonIndex;

    /// <summary>Which content button (if any) contentPoint lands on - shared by TryArmContentButton
    /// and UpdateButtonHover's own hover-tint tracking.</summary>
    protected bool TryGetContentButtonAt(int contentWidth, int contentHeight, Point contentPoint, out int index)
    {
        var buttons = GetContentButtons(contentWidth, contentHeight);
        for (var i = 0; i < buttons.Count; i++)
        {
            if (buttons[i].ContentRect.Contains(contentPoint))
            {
                index = i;
                return true;
            }
        }
        index = -1;
        return false;
    }

    /// <summary>Arms whichever content button contentPoint lands on, if any - a subclass's own
    /// OnMouseDown calls this, typically as the final fallback once Settings/extra-button checks miss.</summary>
    protected bool TryArmContentButton(Point contentPoint)
    {
        var size = GetContentSize();
        if (!TryGetContentButtonAt(size.Width, size.Height, contentPoint, out var index))
            return false;
        _armedContentButtonIndex = index;
        return true;
    }

    /// <summary>Fires whichever content button was armed (see TryArmContentButton), but only if the
    /// mouse is still over that same button on release - a subclass's own OnMouseUp calls this.</summary>
    protected void FireArmedContentButton(Point contentPoint)
    {
        if (_armedContentButtonIndex is not int index)
            return;
        _armedContentButtonIndex = null;

        var size = GetContentSize();
        var buttons = GetContentButtons(size.Width, size.Height);
        if (index < buttons.Count && buttons[index].ContentRect.Contains(contentPoint))
            buttons[index].OnClick();
    }

    /// <summary>Painted unconditionally (unlike PaintExtraButtons/PaintSettingsButton, which only show
    /// while ShowsButtons is true) - content buttons are part of the widget's own always-visible body.</summary>
    private void PaintContentButtons(Graphics g, int contentWidth, int contentHeight)
    {
        var buttons = GetContentButtons(contentWidth, contentHeight);
        if (buttons.Count == 0)
            return;

        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            var buttonRect = ToWindow(button.ContentRect);
            // Same opaque-backing/GDI+-AntiAlias reasoning as PaintSettingsButton's own two comments.
            using (var buttonPath = RoundedRectPath.Full(buttonRect, 6))
            {
                using (var buttonFill = new SolidBrush(ThemedField))
                    g.FillPath(buttonFill, buttonPath);
                PaintHeaderBorderModeOutline(g, buttonPath);
            }

            if (_hoveredButtonKind == HoveredButtonKind.Content && _hoveredButtonIndex == i)
                PaintButtonHoverTint(g, buttonRect);

            var previousTextHint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var textBrush = new SolidBrush(Color.WhiteSmoke))
            using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(button.Label, Font, textBrush, buttonRect, textFormat);
            g.TextRenderingHint = previousTextHint;
        }
    }

    // ---- Generic in-body list (Layout Launcher's saved-layout rows) ----
    //
    // Same split as ContentButton/ChromeButton: the base owns the shared scrolling/layout/paint
    // machinery (now via the shared Scrollbar class - see its own doc comment), a subclass owns what
    // a row actually shows (GetListArea/ListRowCount/ListRowHeight/PaintListRow) since that's
    // inherently subclass-specific, the same way FenceForm's own icon grid owns its own column/cell
    // math while sharing the same Scrollbar underneath.

    /// <summary>Content-relative rect the list occupies - Rectangle.Empty (the default) means "no
    /// list" and none of the machinery below paints/scrolls/hit-tests anything. A subclass computes
    /// this from whatever else shares its body (Layout Launcher: everything between its header and
    /// its own bottom button row).</summary>
    protected virtual Rectangle GetListArea(int contentWidth, int contentHeight) => Rectangle.Empty;

    /// <summary>How many rows the list has right now - 0 means nothing to scroll/paint.</summary>
    protected virtual int ListRowCount => 0;

    /// <summary>Every row's fixed height - the whole mechanism assumes uniform rows, the same way
    /// FenceForm's own icon grid assumes a uniform cell size.</summary>
    protected virtual int ListRowHeight => 24;

    /// <summary>Paints one row's own content - rowRect is already window-space (see ToWindow), already
    /// narrowed to leave room for the scrollbar gutter (only when a scrollbar is actually showing - no
    /// reserved gap when every row already fits), and already GDI+-clipped to the list area (so a
    /// partially-scrolled-off row at the top/bottom edge cuts off cleanly) - a subclass just draws
    /// directly into it with ordinary GDI+ calls (DrawString, not GDI's TextRenderer.DrawText, which
    /// ignores Graphics.Clip - see FenceForm.PaintContent's own comment on that same quirk).</summary>
    protected virtual void PaintListRow(Graphics g, int index, Rectangle rowRect) { }

    private readonly Scrollbar _listScrollbar = new();

    /// <summary>How far the list is currently scrolled down, in pixels - read-only here (mutated only
    /// through TryHandleListMouseDown/UpdateListScrollDrag/HandleListMouseWheel above). A subclass
    /// wanting its own per-row click handling (Layout Launcher's Run/Copy/Delete) needs this to work
    /// out which row a given content point actually lands on - deliberately just this one accessor,
    /// not a full row-hit-testing mechanism, since a subclass's own row layout (icon/button placement
    /// within a row) is exactly the kind of thing that stays subclass-specific.</summary>
    protected int ListScrollOffset => _listScrollbar.Offset;

    private int GetListMaxScroll(Rectangle area)
    {
        if (area.IsEmpty || ListRowCount == 0)
            return 0;
        return Math.Max(0, ListRowCount * ListRowHeight - area.Height);
    }

    /// <summary>A subclass's own OnMouseDown calls this, typically as the final fallback once
    /// Settings/Extra/Content-button checks miss - arms scrollbar-thumb dragging, or pages the track
    /// toward a click (see Scrollbar.TryHandleMouseDown). Returns true if the click landed on the
    /// scrollbar at all, repainting immediately if it was a track-page click (a thumb-drag repaints on
    /// the next UpdateListScrollDrag tick instead).</summary>
    protected bool TryHandleListMouseDown(Point contentPoint)
    {
        var size = GetContentSize();
        var area = GetListArea(size.Width, size.Height);
        var maxScroll = GetListMaxScroll(area);
        if (!_listScrollbar.TryHandleMouseDown(contentPoint, area, maxScroll, ListRowHeight))
            return false;

        Capture = true;
        RenderAndPresent();
        return true;
    }

    /// <summary>A subclass's own OnMouseMove calls this every tick - a no-op unless
    /// TryHandleListMouseDown just armed the thumb.</summary>
    protected void UpdateListScrollDrag(Point contentPoint)
    {
        var size = GetContentSize();
        var area = GetListArea(size.Width, size.Height);
        if (_listScrollbar.UpdateDrag(contentPoint, area, GetListMaxScroll(area)))
            RenderAndPresent();
    }

    /// <summary>A subclass's own OnMouseUp calls this unconditionally - a no-op unless a scrollbar
    /// drag was actually in progress.</summary>
    protected void EndListScrollDrag()
    {
        if (_listScrollbar.EndDrag())
            Capture = false;
    }

    /// <summary>A subclass's own OnMouseWheel calls this with e.Delta - a no-op if the list has
    /// nothing to scroll.</summary>
    protected void HandleListMouseWheel(int delta)
    {
        var size = GetContentSize();
        var area = GetListArea(size.Width, size.Height);
        if (_listScrollbar.HandleWheel(delta, ListRowHeight, GetListMaxScroll(area)))
            RenderAndPresent();
    }

    /// <summary>Painted unconditionally (unlike PaintExtraButtons/PaintSettingsButton, which only show
    /// while ShowsButtons is true) - the list, like ContentButtons, is part of the widget's own
    /// always-visible body.</summary>
    private void PaintList(Graphics g, int contentWidth, int contentHeight)
    {
        var area = GetListArea(contentWidth, contentHeight);
        if (area.IsEmpty)
            return;

        // Re-clamped on every paint (same reasoning as FenceForm.PaintContent's own scrollbar clamp) -
        // a resize changes GetListArea's own height (and so GetListMaxScroll) without going through
        // TryHandleListMouseDown/UpdateListScrollDrag/HandleListMouseWheel, so a scroll offset set
        // before the resize could otherwise sit past the new, smaller max - drawing every row's
        // rowTop from a stale offset that no longer corresponds to a valid scroll position at all.
        var maxScroll = GetListMaxScroll(area);
        _listScrollbar.ClampToMax(maxScroll);

        var hasScrollbar = maxScroll > 0;
        var rowWidth = hasScrollbar ? area.Width - (Scrollbar.Width + Scrollbar.Margin * 2) : area.Width;

        // Same ShowsButtons-wins color choice as PaintHeaderBorderModeOutline/PaintChrome's own
        // outer body border - see PaintHeaderBorderModeOutline's own doc comment for why - shared by
        // the list's own outer border and the divider lines between its rows below, so all three
        // always agree.
        var borderColor = ShowsButtons ? ThemedActiveBorder : ThemedTitle;

        var previousClip = g.Clip;
        g.SetClip(ToWindow(area));
        for (var i = 0; i < ListRowCount; i++)
        {
            var rowTop = area.Top + i * ListRowHeight - _listScrollbar.Offset;
            if (rowTop + ListRowHeight <= area.Top || rowTop >= area.Bottom)
                continue;
            PaintListRow(g, i, ToWindow(new Rectangle(area.Left, rowTop, rowWidth, ListRowHeight)));

            // Header Border Mode's own row divider - a line under every row except the last (whose
            // own bottom edge already coincides with the list's own outer border, drawn below) - the
            // same "tie every element together" idea Header Border Mode already applies to buttons
            // and the list's own outer border, just carried one level deeper into the rows
            // themselves. 2.5f, not the list border's own 1f - a standalone DrawLine at 1f reads
            // visibly fainter than the same-width DrawRectangle below once it's sitting on top of a
            // row's own fill instead of the sharper contrast at the list's outer edge, even though
            // both are nominally the same thickness.
            if (Style.HeaderBorderMode && i < ListRowCount - 1)
            {
                var dividerY = rowTop + ListRowHeight;
                using var dividerPen = new Pen(borderColor, 2.5f);
                g.DrawLine(dividerPen, ToWindow(new Point(area.Left, dividerY)), ToWindow(new Point(area.Left + rowWidth, dividerY)));
            }
        }
        g.Clip = previousClip;
        previousClip.Dispose();

        if (Style.HeaderBorderMode)
        {
            using var borderPen = new Pen(borderColor, 1f);
            g.DrawRectangle(borderPen, ToWindow(area));
        }

        if (_listScrollbar.GetGeometry(area, maxScroll) is { } sb)
            PaintScrollbar(g, sb);
    }

    /// <summary>Draws a Scrollbar.Geometry's own track/thumb - shared by the list above and FenceForm's
    /// own icon-grid scrollbar, both against the same Scrollbar instance-per-widget pattern.</summary>
    protected void PaintScrollbar(Graphics g, Scrollbar.Geometry sb)
    {
        using var trackBrush = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
        g.FillRectangle(trackBrush, ToWindow(new Rectangle(sb.TrackX, sb.TrackTop, Scrollbar.Width, sb.TrackHeight)));

        using var thumbBrush = new SolidBrush(Color.FromArgb(140, 255, 255, 255));
        using var thumbPath = RoundedRectPath.Full(ToWindow(new Rectangle(sb.TrackX, sb.ThumbY, Scrollbar.Width, sb.ThumbHeight)), Scrollbar.Width / 2);
        g.FillPath(thumbBrush, thumbPath);
    }

    /// <summary>Keeps an already-open Settings dropdown anchored to its button after a resize moves
    /// it out from under it - the default (and, so far, only) OnResized follow-up any subclass needs.</summary>
    protected virtual void OnResized()
    {
        if (SettingsDropdown is null)
            return;
        var contentSize = GetContentSize();
        var onLeft = ShouldSettingsButtonOpenLeft(contentSize.Width);
        var buttonRect = GetSettingsButtonRect(contentSize.Width, onLeft);
        var buttonScreenRect = new Rectangle(PointToScreen(ToWindow(buttonRect.Location)), buttonRect.Size);
        SettingsDropdown.RepositionRelativeTo(buttonScreenRect, preferLeft: onLeft);
    }

    // ---- Move/resize/snap ----

    protected virtual int ResizeMargin => 12;

    /// <summary>Whether this window can be resized at all - true by default (every widget on this
    /// base gets full resize unless it deliberately opts out). See ResizableEdges to restrict which
    /// edges instead of turning resize off entirely.</summary>
    protected virtual bool SupportsResize => true;

    /// <summary>Which edges (see SnapEdges) resize when SupportsResize - all four by default.</summary>
    protected virtual SnapEdges ResizableEdges => SnapEdges.Left | SnapEdges.Right | SnapEdges.Top | SnapEdges.Bottom;

    /// <summary>The subclass's own current body rect (position + size), read fresh once at the start
    /// of every drag/resize (see DragStartBody) - the fixed anchor everything that tick measures
    /// against instead of the OS's own incrementally-drifting proposed rect.</summary>
    protected abstract Rectangle GetCurrentBody();

    /// <summary>What this widget offers as its OWN edges when a different widget is the one
    /// dragging/resizing and looking for something to snap against (see GetOtherWidgetEdges) -
    /// GetCurrentBody() itself by default. Deliberately a separate hook rather than just reusing
    /// GetCurrentBody() directly there: that method is also the fixed anchor THIS widget's own
    /// drag/resize/hit-test math measures against every tick (see DragStartBody/WriteBackWindowRect),
    /// which has to keep meaning exactly "the plain body, matching OuterMargin/TopBand/BottomBand" -
    /// changing what it returns would throw that off. A subclass whose visible silhouette extends
    /// beyond its own plain body in a direction another widget should still be able to snap flush
    /// against (FolderFenceForm's own folder tab, poking up above the header - see PaintFolderTab)
    /// overrides this instead, leaving GetCurrentBody() itself untouched.</summary>
    protected virtual Rectangle GetSnapTargetBody() => GetCurrentBody();

    /// <summary>This widget's own margin setting (IWidgetStyle.Margin) - how far it prefers to keep
    /// from another fence's edge or a custom snap line while dragging/resizing.</summary>
    protected abstract int SnapMargin { get; }

    private static bool IsResizeHitTestCode(int hitTest) =>
        hitTest is HTLEFT or HTRIGHT or HTTOP or HTBOTTOM or HTTOPLEFT or HTTOPRIGHT or HTBOTTOMLEFT or HTBOTTOMRIGHT;

    /// <summary>WM_SIZING's wParam - a flat enumeration, not a bitfield (the four corner values
    /// don't decompose into their two edges by combining the single-edge values).</summary>
    private static SnapEdges SnapEdgesFromWmSz(int wmsz) => wmsz switch
    {
        NativeMethods.WMSZ_LEFT => SnapEdges.Left,
        NativeMethods.WMSZ_RIGHT => SnapEdges.Right,
        NativeMethods.WMSZ_TOP => SnapEdges.Top,
        NativeMethods.WMSZ_TOPLEFT => SnapEdges.Top | SnapEdges.Left,
        NativeMethods.WMSZ_TOPRIGHT => SnapEdges.Top | SnapEdges.Right,
        NativeMethods.WMSZ_BOTTOM => SnapEdges.Bottom,
        NativeMethods.WMSZ_BOTTOMLEFT => SnapEdges.Bottom | SnapEdges.Left,
        NativeMethods.WMSZ_BOTTOMRIGHT => SnapEdges.Bottom | SnapEdges.Right,
        _ => SnapEdges.None,
    };

    /// <summary>Resize hit-testing for the ResizeMargin-wide band just outside the visible body,
    /// respecting SupportsResize/ResizableEdges - returns the matching HT* edge/corner code, or null
    /// if the point isn't in a resize band (or resize isn't available there at all). windowPoint is
    /// window-relative; width/height are the window's own full current size.</summary>
    protected int? ResizeHitTest(Point windowPoint, int width, int height)
    {
        if (!SupportsResize)
            return null;

        var edges = ResizableEdges;
        var band = OuterMargin + ResizeMargin;
        var left = edges.HasFlag(SnapEdges.Left) && windowPoint.X <= band;
        var right = edges.HasFlag(SnapEdges.Right) && windowPoint.X >= width - band;
        var top = edges.HasFlag(SnapEdges.Top) && windowPoint.Y <= TopBand + ResizeMargin;
        var bottom = edges.HasFlag(SnapEdges.Bottom) && windowPoint.Y >= height - BottomBand - ResizeMargin;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return null;
    }

    /// <summary>Re-inflates a snapped visible-body rect back into raw window coordinates and writes
    /// it into the RECT at lParam for the OS's own move/resize loop to pick up.</summary>
    protected void WriteBackWindowRect(IntPtr lParam, Rectangle body)
    {
        var rect = new RECT
        {
            Left = body.Left - OuterMargin,
            Top = body.Top - TopBand,
            Right = body.Right + OuterMargin,
            Bottom = body.Bottom + BottomBand,
        };
        Marshal.StructureToPtr(rect, lParam, false);
    }

    /// <summary>Candidate snap positions from every other currently-live LayeredWidgetForm's own edges
    /// (any fence, the Layout Launcher, any future widget built on this base) - Left/Right (vertical,
    /// for X-axis snapping) and Top/Bottom (horizontal, for Y-axis snapping) - for SnapLineManager.
    /// SnapMove/SnapResize. This widget's own SnapMargin (not each candidate's own) applies, like a
    /// CSS margin: every other widget's edge also contributes a second candidate offset outward by
    /// that amount alongside the flush one - SnapEngine's own nearest-candidate-wins logic picks
    /// whichever of the two is actually closest. Not filtered by Visible - a hidden widget ("Show/Hide
    /// All") is still a valid snap target the same way FenceForm's own predecessor of this method
    /// never bothered filtering for that either. GetSnapTargetBody() is read fresh per candidate (not
    /// cached anywhere) so a widget still mid-drag itself contributes its own latest position. Empty
    /// outright when Widget Manager's own Widget Snapping switch is off (Fences.SnapLines.
    /// WidgetEdgesEnabled) - the sole gate, so every caller (ComputeMovedBody/ComputeResizedBody/
    /// BeginSnapDrag) picks that up for free.</summary>
    private (IReadOnlyList<int> Vertical, IReadOnlyList<int> Horizontal) GetOtherWidgetEdges()
    {
        if (!Fences.SnapLines.WidgetEdgesEnabled)
            return (Array.Empty<int>(), Array.Empty<int>());

        var margin = SnapMargin;
        var vertical = new List<int>();
        var horizontal = new List<int>();

        foreach (var widget in _liveWidgets)
        {
            if (ReferenceEquals(widget, this))
                continue;

            var bounds = widget.GetSnapTargetBody();
            vertical.Add(bounds.Left);
            vertical.Add(bounds.Right);
            horizontal.Add(bounds.Top);
            horizontal.Add(bounds.Bottom);

            if (margin > 0)
            {
                vertical.Add(bounds.Left - margin);
                vertical.Add(bounds.Right + margin);
                horizontal.Add(bounds.Top - margin);
                horizontal.Add(bounds.Bottom + margin);
            }
        }

        return (vertical, horizontal);
    }

    /// <summary>Snaps a proposed move against every other widget's edges (see GetOtherWidgetEdges) and
    /// this app's custom snap lines - the default every subclass gets for free. Holding the right
    /// mouse button down at the same time hides the widget-edge candidates for as long as it's held,
    /// leaving just the custom lines (checked live via Control.MouseButtons, not any button-down
    /// message, since DefWindowProc's own modal move loop may never route one to this WndProc at all
    /// while it's running).</summary>
    protected virtual Rectangle ComputeMovedBody(Rectangle proposedBody)
    {
        IReadOnlyList<int> vCandidates = Array.Empty<int>();
        IReadOnlyList<int> hCandidates = Array.Empty<int>();
        if ((MouseButtons & MouseButtons.Right) == 0)
            (vCandidates, hCandidates) = GetOtherWidgetEdges();
        return Fences.SnapLines.SnapMove(proposedBody, vCandidates, hCandidates, SnapMargin).Rect;
    }

    /// <summary>Same idea as ComputeMovedBody, for a resize - always shows both custom lines and
    /// widget edges (WM_SIZING has no right-click modifier the way a move does).</summary>
    protected virtual Rectangle ComputeResizedBody(Rectangle proposedBody, SnapEdges activeEdges)
    {
        var (vCandidates, hCandidates) = GetOtherWidgetEdges();
        return Fences.SnapLines.SnapResize(proposedBody, activeEdges, vCandidates, hCandidates, SnapMargin).Rect;
    }

    /// <summary>WM_ENTERSIZEMOVE's own snap-guide setup, shown for the guide overlay's whole
    /// lifetime - a resize (see IsResizing) always shows both custom lines and widget edges; a move
    /// shows both too unless right is already held right at the start of the drag (the common case
    /// is checked live every tick inside ComputeMovedBody instead; this is only for the very first
    /// frame, before any movement has happened yet, so the guides don't lag one tick behind).</summary>
    protected virtual void BeginSnapDrag()
    {
        if (IsResizing || (MouseButtons & MouseButtons.Right) == 0)
        {
            var (vGuides, hGuides) = GetOtherWidgetEdges();
            var monitor = Screen.FromRectangle(DragStartBody).Bounds;
            Fences.SnapLines.BeginDrag(includeCustomLines: true, vGuides, hGuides, monitor);
        }
        else
        {
            Fences.SnapLines.BeginDrag();
        }
    }

    /// <summary>WM_EXITSIZEMOVE's own subclass-specific follow-up: persisting the settled position/
    /// size, any "tidy up now that the drag is done" behavior, z-order restacking - all entirely up
    /// to the override (the snap-guide teardown and IsMoving/IsResizing reset happen automatically
    /// around this call - see WndProc). This one hook is what keeps all of that optional for anything
    /// this base doesn't itself know about.</summary>
    protected abstract void OnDragEnd();

    // ---- Hooks a subclass must (or may) implement ----

    protected abstract int HitTest(IntPtr lParam);

    /// <summary>Fires after Activation's own WM_NCLBUTTONDOWN handling, with the same raw hit-test
    /// code HitTest returned for this point - default sets IsResizing from it (see
    /// IsResizeHitTestCode), which is all FenceForm's own equivalent ever did; override only for
    /// something beyond that.</summary>
    protected virtual void OnNcLButtonDown(int hitTestCode) => IsResizing = IsResizeHitTestCode(hitTestCode);

    /// <summary>Fires after WM_RBUTTONUP's own Activation.Activate() - a right-click landing
    /// somewhere in the plain client body (not the margin/title row, which go through
    /// WM_NCRBUTTONDOWN/ShowTitleContextMenu instead) still needs to activate the widget by default;
    /// this is where a subclass with something worth right-clicking in its own content (a Fence's
    /// items, say) can show its own context menu. contentPoint is already in content space (see
    /// ToContent). Default no-op - right-click still activates either way.</summary>
    protected virtual void OnClientRightClick(Point contentPoint) { }

    /// <summary>Paints everything beyond the near-transparent margin band this base already filled -
    /// body, title, buttons, and whatever content fills the rest. contentWidth/contentHeight are the
    /// visible body's own size (see GetContentSize) - use ToWindow to place anything drawn here.</summary>
    protected abstract void PaintContent(Graphics g, int contentWidth, int contentHeight);

    /// <summary>Dispose(true)'s own subclass-specific contents (a drag ghost, an icon cache -
    /// whatever the subclass owns beyond what this base already tracks, which now includes the
    /// rename box, title context menu, and Settings dropdown). Called before this base tears down
    /// RenderOpacity/the theme brush.</summary>
    protected abstract void DisposeOwnedResources();

    // ---- Title / rename ----

    /// <summary>The rename-able title text itself - get returns what's currently shown/being
    /// renamed; set commits a new value (already trimmed and validated by BeginRename's own commit
    /// handler) and is responsible for persisting it, the same way a subclass persists its other
    /// settings.</summary>
    protected abstract string Title { get; set; }

    /// <summary>What actually renders in the title band and drives its own click/hit-test rect
    /// (TitleTextRect) - Title itself by default, for every widget with no reason to show anything
    /// but its own bare rename-able name. A subclass whose header needs to show more than that
    /// (FolderFenceForm's own breadcrumb while browsing a subfolder, say) overrides this instead of
    /// Title itself, so BeginRename/OnRenameCommit's own EditBox still always seeds from and commits
    /// to the real underlying name - never whatever extra text is only ever shown, not editable.</summary>
    protected virtual string DisplayTitle => Title;

    /// <summary>Extra left padding added on top of the title row's own fixed 8px margin - room for a
    /// subclass's own hand-painted control sitting to the left of the title text inside the header
    /// row itself (FolderFenceForm's own back button, say). 0 by default, meaning the title text
    /// starts exactly where it always has for every other widget on this base.</summary>
    protected virtual int TitleTextInset => 0;

    /// <summary>How much of contentWidth the title row's own text-layout math (TitleRowAvailableWidth/
    /// TitleTextRect) should treat as available - contentWidth itself by default. A subclass whose
    /// header isn't a plain full-width band (FolderFenceForm's own folder-tab header, whose title
    /// text needs to stay within the tab portion rather than spill into the transparent step down to
    /// the lower shoulder) overrides this instead.</summary>
    protected virtual int TitleRowWidth(int contentWidth) => contentWidth;

    /// <summary>Content-space height of the title row.</summary>
    protected abstract int TitleRowHeight { get; }

    /// <summary>The body's own outline shape, content-relative in window space (see ToWindow) - a
    /// plain rounded rectangle by default, used both for the border stroke around the whole widget
    /// and (when TitleVisible is false) the body's own fill. A subclass whose window silhouette
    /// isn't a plain rounded rectangle (FolderFenceForm's own folder-tab header, say) overrides this
    /// together with GetHeaderFillPath below, so the border stroke/fill still trace one consistent
    /// shape without this base needing to know anything about what that shape actually is. Caller
    /// owns disposal.</summary>
    protected virtual GraphicsPath GetBodyOutlinePath(int contentWidth, int contentHeight, int cornerRadius) =>
        RoundedRectPath.Full(ToWindow(new Rectangle(0, 0, contentWidth - 1, contentHeight - 1)), cornerRadius);

    /// <summary>The header/title band's own fill shape, content-relative in window space - rounded
    /// on the top two corners only by default (RoundedRectPath.Top). Used for the header's own fill
    /// and (when Style.LightBorder is on) its separate outline stroke, so both always agree with
    /// whatever GetBodyOutlinePath's own header portion looks like. Caller owns disposal.</summary>
    protected virtual GraphicsPath GetHeaderFillPath(int contentWidth, int cornerRadius) =>
        RoundedRectPath.Top(ToWindow(new Rectangle(0, 0, contentWidth - 1, TitleRowHeight)), cornerRadius);

    /// <summary>Same family as Control.Font, sized to Style.TitleFontSize (see the "Header" flyout,
    /// BuildHeaderSettingsRows) - only the title text itself, its rename hit-test measurement, and
    /// its rename EditBox use this instead of the plain Font property, since title font size is
    /// title-only, not a whole-window setting (every other themed element - the rename box's own
    /// chrome aside, the Settings dropdown, item labels - still uses Font unchanged).</summary>
    protected Font TitleFont
    {
        get
        {
            var size = Style.TitleFontSize;
            if (_titleFont is null || _titleFontSize != size)
            {
                _titleFont?.Dispose();
                _titleFont = new Font(Font.FontFamily, size);
                _titleFontSize = size;
            }
            return _titleFont;
        }
    }

    /// <summary>How much width the title row's own text (or its rename edit box, see BeginRename)
    /// has to work with, content-relative - contentWidth minus the 8px margin either side, minus the
    /// header close button's own reserved space when it's showing (see GetHeaderCloseButtonRect).
    /// Shared by PaintChrome's own title draw and TitleTextRect/BeginRename below so all three can
    /// never disagree about where the text is allowed to go.</summary>
    private int TitleRowAvailableWidth(int contentWidth) =>
        Math.Max(0, (ShowHeaderCloseButton ? GetHeaderCloseButtonRect(contentWidth).Left - 4 : TitleRowWidth(contentWidth) - 8) - 8 - TitleTextInset);

    /// <summary>The exact rect the title text renders into, content-relative - shifted per
    /// Style.TitleAlignment (Left/Center/Right) to match PaintChrome's own StringFormat-driven
    /// placement, so hit-testing (IsOverTitleRow) always agrees with what's actually drawn - a click
    /// past the end of a short/off-center title doesn't count as "on" it.</summary>
    private Rectangle TitleTextRect(int contentWidth)
    {
        var available = TitleRowAvailableWidth(contentWidth);
        var textWidth = Math.Min(available, TextRenderer.MeasureText(DisplayTitle, TitleFont).Width);
        var left = 8 + TitleTextInset;
        var x = Style.TitleAlignment switch
        {
            TitleAlignment.Center => left + (available - textWidth) / 2,
            TitleAlignment.Right => left + available - textWidth,
            _ => left,
        };
        return new Rectangle(x, 0, textWidth, TitleRowHeight);
    }

    /// <summary>Whether lParam lands specifically on the rendered title text - not just anywhere in
    /// the header row - gating right-click-to-rename/double-click-to-rename to the text itself.
    /// Mirrors the actual title-text paint position (see PaintContent's own title-drawing call, which
    /// should match this rect).</summary>
    protected virtual bool IsOverTitleRow(IntPtr lParam)
    {
        if (!TitleVisible || !NativeMethods.GetWindowRect(Handle, out var rect))
            return false;

        var content = ToContent(ScreenLParamToWindowPoint(lParam, rect));
        return TitleTextRect(GetContentSize().Width).Contains(content);
    }

    protected virtual void BeginRename()
    {
        if (_renameBox is not null || !TitleVisible)
            return;

        var maxWidth = TitleRowAvailableWidth(GetContentSize().Width);
        var rect = ToWindow(new Rectangle(6, 3, maxWidth, Math.Max(0, TitleRowHeight - 6)));
        _renameBox = new EditBox(Handle, Title, ToScreen(rect), TitleFont);
        _renameBox.Commit += OnRenameCommit;
        _renameBox.Cancel += OnRenameCancel;
    }

    private void OnRenameCommit(string newName)
    {
        _renameBox?.Dispose();
        _renameBox = null;

        newName = newName.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != Title)
            Title = newName;

        RenderAndPresent();
    }

    private void OnRenameCancel()
    {
        _renameBox?.Dispose();
        _renameBox = null;
        RenderAndPresent();
    }

    /// <summary>Right-click on the title text specifically (see IsOverTitleRow) - a themed
    /// ContextMenuStrip with a single "Rename" item.</summary>
    protected virtual void ShowTitleContextMenu()
    {
        if (!TitleVisible)
            return;

        _titleContextMenu ??= BuildTitleContextMenu();
        NativeMethods.GetCursorPos(out var pt);
        _titleContextMenu.Show(this, PointToClient(new Point(pt.X, pt.Y)));
    }

    private ContextMenuStrip BuildTitleContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(() => ChromeMenuFieldColor, () => ChromeMenuHoverColor, () => AppTheme.Text),
            Font = Font,
        };
        menu.Items.Add("Rename", null, (_, _) => BeginRename());
        return menu;
    }

    // ---- Settings button/dropdown ----

    protected virtual int HeaderCloseButtonSize => 18;

    /// <summary>Content-relative rect for the always-visible header close glyph (see
    /// ShowHeaderCloseButton/PaintChrome's own close-glyph draw) - anchored to the title row's own
    /// top-right corner regardless of Style.TitleAlignment, the same way a real window's close
    /// button never moves with its title text. Unlike GetSettingsButtonRect below, lives inside the
    /// title row itself rather than the reserved button band above/below it, since it needs to be
    /// visible without the widget being "engaged" first.</summary>
    protected Rectangle GetHeaderCloseButtonRect(int contentWidth) => new(
        contentWidth - 6 - HeaderCloseButtonSize, (TitleRowHeight - HeaderCloseButtonSize) / 2,
        HeaderCloseButtonSize, HeaderCloseButtonSize);

    /// <summary>Whether contentPoint currently sits on the header close glyph - false whenever it
    /// wouldn't even be painted (HideHeader on, or ShowHeaderCloseButton off), so callers don't need
    /// to repeat that guard themselves.</summary>
    protected bool IsOverHeaderCloseButton(Point contentPoint) =>
        TitleVisible && ShowHeaderCloseButton && GetHeaderCloseButtonRect(GetContentSize().Width).Contains(contentPoint);

    // Same arm-then-fire pattern as _armedExtraButtonIndex above.
    private bool _headerCloseButtonArmed;

    /// <summary>Arms the header close button if contentPoint lands on it - a subclass's own
    /// OnMouseDown calls this, typically first (it's reachable even while ShowsButtons is false,
    /// unlike every other chrome button this base owns).</summary>
    protected bool TryArmHeaderCloseButton(Point contentPoint)
    {
        if (!IsOverHeaderCloseButton(contentPoint))
            return false;
        _headerCloseButtonArmed = true;
        return true;
    }

    /// <summary>What clicking the header close button actually does - Close() by default, which is
    /// correct for any widget whose own OnFormClosing already cancels-and-hides (WidgetManagerWidget/
    /// LayoutLauncherWidget), or one like ReadmeWidget that genuinely wants a real close. FenceForm
    /// overrides this instead of getting Close()'s default behavior: it has no OnFormClosing of its
    /// own to intercept a raw Close() (a fence's lifecycle is delete-or-nothing, owned by
    /// FenceManager.DeleteFence, not a per-fence hide - FenceManager doesn't even subscribe to
    /// FormClosed on its own fence windows), so a plain Close() here would just destroy the window
    /// while leaving FenceManager's own _models/_forms still holding it.</summary>
    protected virtual void OnHeaderCloseButtonClick() => Close();

    /// <summary>Fires the header close button if it was armed (see TryArmHeaderCloseButton) and the
    /// mouse is still over it on release - a subclass's own OnMouseUp calls this.</summary>
    protected void FireArmedHeaderCloseButton(Point contentPoint)
    {
        if (!_headerCloseButtonArmed)
            return;
        _headerCloseButtonArmed = false;
        if (IsOverHeaderCloseButton(contentPoint))
            OnHeaderCloseButtonClick();
    }

    protected virtual int SettingsButtonWidth => 64;
    protected virtual int SettingsButtonHeight => 22;
    protected virtual int SettingsButtonGap => 6;

    /// <summary>Extra push further out from the body, on top of SettingsButtonGap, for the whole
    /// Settings/Copy Settings/ExtraButtons row (every one of them chains its own Y off
    /// GetSettingsButtonRect below, so this one hook moves them all together) - 0 by default. Only
    /// meaningful in the not-flipped (top) case; a subclass has no reason to need it in the
    /// ButtonRowAtBottom case too, since that's a plain body edge with nothing else living there.
    /// FolderFenceForm's own folder tab (see PaintFolderTab) is the reason this exists: without it,
    /// the button row and the tab both want roughly the same content-Y band above the header,
    /// overlapping/visually colliding whenever the button happens to be on the same side as the
    /// tab (its default top-right placement is usually clear of it, but flips to top-left - see
    /// ShouldSettingsButtonOpenLeft - whenever the Settings dropdown wouldn't otherwise fit).</summary>
    protected virtual int SettingsButtonRowInset => 0;

    /// <summary>Content-relative, positioned just outside the visible body, in the reserved button
    /// band - lives outside the visible body entirely, right down to the Y formula (negative - above
    /// content Y=0 normally, or below the body's own bottom edge instead once ButtonRowAtBottom
    /// flips there). Flush with the top-right corner by default; flipped to the top-left when the
    /// options dropdown wouldn't fit opening rightward from there (see ShouldSettingsButtonOpenLeft,
    /// which reuses this same rect's X to decide which side the menu itself opens on, so the two
    /// always agree).</summary>
    protected Rectangle GetSettingsButtonRect(int contentWidth, bool onLeft)
    {
        var y = ButtonRowAtBottom
            ? GetContentSize().Height + SettingsButtonGap
            : -(SettingsButtonHeight + SettingsButtonGap + SettingsButtonRowInset);
        return onLeft
            ? new Rectangle(0, y, SettingsButtonWidth, SettingsButtonHeight)
            : new Rectangle(contentWidth - SettingsButtonWidth, y, SettingsButtonWidth, SettingsButtonHeight);
    }

    /// <summary>Copy Settings' own declared width (see CopySettingsButton) - kept as its own virtual
    /// property, not just an inline literal, in case a future subclass ever needs to widen it the way
    /// SettingsButtonWidth/SettingsButtonHeight are already overridable.</summary>
    protected virtual int CopySettingsButtonWidth => 22;

    /// <summary>Measures the actual options menu (BuildSettingsRows) against the screen this window
    /// is currently on, using the button's default top-right placement as the anchor - i.e. "would
    /// the menu fit opening to the right of a right-corner button".</summary>
    protected bool ShouldSettingsButtonOpenLeft(int contentWidth)
    {
        var rightAligned = ToWindow(GetSettingsButtonRect(contentWidth, onLeft: false));
        var buttonScreenRect = new Rectangle(PointToScreen(rightAligned.Location), rightAligned.Size);
        return StyleMenuRows.ShouldOpenLeft(buttonScreenRect, BuildSettingsRows(), Font);
    }

    /// <summary>Opens (or, if one's already open, replaces) the Settings dropdown. Explicit
    /// RenderOpacity.BeginIfNeeded() calls on both open and close - TargetOpacity typically depends
    /// on SettingsDropdown being non-null, so each transition may need to start easing toward/away
    /// from Full Opacity right away rather than waiting for some unrelated repaint to notice.</summary>
    protected void OpenSettingsMenu()
    {
        SettingsDropdown?.Dispose();

        var width = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(width);
        var buttonScreenRect = RectangleToScreen(ToWindow(GetSettingsButtonRect(width, onLeft)));
        var dropdown = new DropdownMenu(BuildSettingsRows(), buttonScreenRect, onLeft, Font,
            () => SettingsMenuFieldColor, () => SettingsMenuHoverColor, () => SettingsMenuAccentColor,
            () => SettingsMenuBorderColor, () => SettingsMenuTooltipColor);
        SettingsDropdown = dropdown;
        dropdown.ItemClicked += id =>
        {
            HandleSettingsCommand(id);
            dropdown.RefreshChecks();
        };
        Activation.MenuOpen = true;
        dropdown.FormClosed += (_, _) =>
        {
            SettingsDropdown = null;
            Activation.MenuOpen = false;
            RenderOpacity.BeginIfNeeded();
        };
        dropdown.Show(this);
        RenderOpacity.BeginIfNeeded();
    }

    // ---- Base-owned behavior ----

    /// <summary>Same "losing focus always deactivates" rule regardless of what's currently showing -
    /// see WidgetActivation's own doc comment for why activation itself is never driven by the
    /// Control's own Activated/OnActivated instead.</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Activation.Deactivate();
    }

    /// <summary>Tracks whether the cursor is over this window's client area, for "Full Opacity When
    /// Active" (see IsHovered/TargetOpacity) - client-area only; the margin/resize band is covered
    /// separately by WM_NCMOUSEMOVE/WM_NCMOUSELEAVE below.</summary>
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isClientHovered = true;
        RenderOpacity.BeginIfNeeded();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isClientHovered = false;
        RenderOpacity.BeginIfNeeded();
        UpdateButtonHover(null);
    }

    /// <summary>Base-owned so every subclass's Settings/ChromeButton/ContentButton get the same hover
    /// tint (see PaintButtonHoverTint) for free - a subclass overriding OnMouseMove for its own
    /// purposes (FenceForm's icon-grid hover, drag-arming) already calls base.OnMouseMove(e) first,
    /// same as every other override in this class.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateButtonHover(ToContent(e.Location));
    }

    /// <summary>Recomputes which button (if any) contentPoint sits over, repainting only on an actual
    /// change - null means "not hovering the client area at all" (see OnMouseLeave). Settings/Extra
    /// take priority over Content since they can visually overlap in a very small/narrow widget.
    /// Also drives _chromeButtonTooltip - Copy Settings (AllBarButtons[0]) is icon-only (a hand-drawn
    /// eyedropper glyph, no text label of its own - see PaintEyedropperGlyph), and any other
    /// AllBarButtons entry with a custom PaintGlyph (a Fence's own Copy/Delete squares) is exactly the
    /// same situation, so every one of them shares this one tooltip rather than each needing a
    /// separate copy of it - there's no dedicated HoveredButtonKind.CopySettings case anymore, Copy
    /// Settings is just index 0 of the same Extra chain now.</summary>
    private void UpdateButtonHover(Point? contentPoint)
    {
        var kind = HoveredButtonKind.None;
        var index = -1;
        Rectangle extraButtonRect = default;

        if (contentPoint is Point point)
        {
            var size = GetContentSize();
            var onLeft = ShouldSettingsButtonOpenLeft(size.Width);

            if (ShowsButtons && GetSettingsButtonRect(size.Width, onLeft).Contains(point))
            {
                kind = HoveredButtonKind.Settings;
            }
            else if (ShowsButtons && TryGetExtraButtonAt(size.Width, onLeft, point, out var extraIndex))
            {
                kind = HoveredButtonKind.Extra;
                index = extraIndex;
                extraButtonRect = GetExtraButtonRect(size.Width, onLeft, extraIndex);
            }
            // Not gated by ShowsButtons, unlike every check above - the header close button is meant
            // to be reachable (and show hover feedback) without the widget being engaged first.
            else if (IsOverHeaderCloseButton(point))
            {
                kind = HoveredButtonKind.HeaderClose;
            }
            else if (TryGetContentButtonAt(size.Width, size.Height, point, out var contentIndex))
            {
                kind = HoveredButtonKind.Content;
                index = contentIndex;
            }
        }

        var tooltipText = kind == HoveredButtonKind.Extra ? AllBarButtons[index].EffectiveTooltip : null;
        var tooltipChanged = tooltipText is not null
            ? _chromeButtonTooltip.Show(tooltipText, ToWindow(extraButtonRect))
            : _chromeButtonTooltip.Hide();

        if (kind == _hoveredButtonKind && index == _hoveredButtonIndex && !tooltipChanged)
            return;

        _hoveredButtonKind = kind;
        _hoveredButtonIndex = index;
        RenderAndPresent();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Set before anything below runs - see IsDisposing's own field comment.
            IsDisposing = true;
            _liveWidgets.Remove(this);
            _renameBox?.Dispose();
            _titleContextMenu?.Dispose();
            SettingsDropdown?.Dispose();
            _titleFont?.Dispose();
            DisposeOwnedResources();
            RenderOpacity.Dispose();
            if (_themeBrush != IntPtr.Zero)
                NativeMethods.DeleteObject(_themeBrush);
        }
        base.Dispose(disposing);
    }

    // Everything is painted at this many times the window's own pixel size, then downsampled back
    // down under HighQualityBicubic before Present - plain GDI+ AntiAlias/AntiAliasGridFit still
    // only has one sample per final pixel to work with, so small text stays visibly softer than
    // ClearType would render it; supersampling gives the resampler several samples per final pixel
    // instead, which sharpens glyph edges (and every other antialiased edge - icons, rounded
    // corners) without touching ClearType's own premultiplied-alpha problems at all. Only safe now
    // that every DrawText call reachable from PaintContent has been converted to GDI+'s own
    // DrawString (see LayeredWidgetForm.PaintChrome's title and FenceForm.PaintItems' icon label) -
    // GDI's TextRenderer.DrawText ignores Graphics.Transform entirely, so it rendered at 1x size in
    // the wrong (unscaled) position once this was in place, before that conversion. Cost is
    // quadratic in this value (buffer pixel count, plus the downsample pass) and every widget on
    // this base repaints on hover/drag, not just on demand - watch drag responsiveness if this goes
    // higher still.
    private const float SupersampleScale = 3f;

    /// <summary>Builds this window's full appearance into an off-screen ARGB bitmap and pushes it via
    /// UpdateLayeredWindow. Called any time something visible changes (hover, drag, rename, content)
    /// rather than in response to WM_PAINT, since a layered window's content isn't repainted by
    /// Windows itself.</summary>
    protected void RenderAndPresent()
    {
        if (IsDisposing)
            return;

        if (!NativeMethods.GetWindowRect(Handle, out var windowRect))
            return;

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        var contentWidth = width - OuterMargin * 2;
        var contentHeight = height - TopBand - BottomBand;
        if (contentWidth <= 0 || contentHeight <= 0)
            return;

        var scaledWidth = (int)Math.Ceiling(width * SupersampleScale);
        var scaledHeight = (int)Math.Ceiling(height * SupersampleScale);

        using var scaledBuffer = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaledBuffer))
        {
            g.Clear(Color.Transparent);
            g.ScaleTransform(SupersampleScale, SupersampleScale);

            // Needs a non-zero (if faint) alpha - Windows treats fully transparent (alpha 0) pixels
            // of a layered window as click-through, so a truly invisible margin couldn't receive the
            // drag/resize hit-testing it exists for. Drawn first; PaintContent's own opaque body then
            // covers all of it except the margin itself.
            using (var marginFill = new SolidBrush(MarginFillColor))
                g.FillRectangle(marginFill, 0, 0, width, height);

            g.SetClip(new Rectangle(0, 0, width, height));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // DrawIcon's native GDI stretch looks jagged when scaling a source icon down - drawing
            // icons as bitmaps under high-quality interpolation instead avoids that.
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            PaintContent(g, contentWidth, contentHeight);
        }

        using var buffer = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(buffer))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(scaledBuffer, new Rectangle(0, 0, width, height));
        }

        LayeredWindowPresenter.Present(Handle, buffer, new Point(windowRect.Left, windowRect.Top), RenderOpacity.Value,
            GetFullOpacityRegions(contentWidth, contentHeight));
    }

    /// <summary>Window-space rects (see ToWindow) that should render at full opacity regardless of
    /// Style.Opacity - the Settings button, whichever AllBarButtons currently fit on the bar (Copy
    /// Settings plus a subclass's own ExtraButtons - see VisibleExtraButtonCount), _chromeButtonTooltip
    /// itself while it's showing (it belongs to one of those bar buttons - without this it visibly
    /// washed out against the button it's pointing at, which stays solid), and whatever a subclass's
    /// own AdditionalFullOpacityRegions contributes - the same "always fully visible" treatment the
    /// Settings dropdown already gets for free just by being a separate window. Null while ShowsButtons
    /// is false (nothing to exempt - none of these are even painted then).</summary>
    private List<Rectangle>? GetFullOpacityRegions(int contentWidth, int contentHeight)
    {
        if (!ShowsButtons)
            return null;

        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        var regions = new List<Rectangle> { ToWindow(GetSettingsButtonRect(contentWidth, onLeft)) };

        // Only whichever buttons are actually painted on the bar right now (see
        // VisibleExtraButtonCount) - no region needed for one that currently isn't there.
        var visibleCount = VisibleExtraButtonCount(contentWidth);
        for (var i = 0; i < visibleCount; i++)
            regions.Add(ToWindow(GetExtraButtonRect(contentWidth, onLeft, i)));

        // Same bounds argument PaintChrome's own _chromeButtonTooltip.Paint call passes - has to
        // match exactly, or this would exempt a different rect than the one actually painted.
        if (_chromeButtonTooltip.GetPillRect(Font, ToWindow(new Rectangle(0, 0, contentWidth, contentHeight))) is { } tooltipRect)
            regions.Add(tooltipRect);

        regions.AddRange(AdditionalFullOpacityRegions(contentWidth));

        return regions;
    }

    /// <summary>A subclass's own hand-drawn buttons that aren't ChromeButtons (a Fence's own New/
    /// Delete squares, which need custom glyphs rather than a plain text label - see ChromeButton's
    /// own doc comment on why those stay separate) but still want the same "always fully visible"
    /// treatment as Settings/ChromeButton above. Only ever called while ShowsButtons is already true,
    /// since that's the only time these are painted at all. Empty by default.</summary>
    protected virtual IEnumerable<Rectangle> AdditionalFullOpacityRegions(int contentWidth) => Array.Empty<Rectangle>();

    /// <summary>Intercepts the OS's own non-client/interactive-move/resize handling. WM_MOVING/
    /// WM_SIZING are handled directly here now (see ComputeMovedBody/ComputeResizedBody) - DefWindowProc
    /// has no default handling for either, so mutating the RECT at lParam and returning is enough;
    /// the outer drag loop (not DefWindowProc) is what reads it back. Everything else follows
    /// FenceForm's own original WndProc structure: messages DefWindowProc has no default handling
    /// worth keeping (WM_NCHITTEST, WM_NCLBUTTONDBLCLK, WM_NCRBUTTONDOWN, WM_CTLCOLOREDIT) are
    /// swallowed and returned early; WM_NCLBUTTONDOWN/WM_NCMOUSEMOVE/WM_NCMOUSELEAVE still need the
    /// default proc afterward, so they fall through instead of returning.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_ERASEBKGND)
        {
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == WM_PAINT)
        {
            // Content is pushed via UpdateLayeredWindow (RenderAndPresent), not drawn in response to
            // WM_PAINT - every subclass on this base needs this same swallow, or Windows keeps
            // re-posting the message. Just clear the update region so it stops.
            NativeMethods.BeginPaint(Handle, out var ps);
            NativeMethods.EndPaint(Handle, ref ps);
            return;
        }

        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = (IntPtr)HitTest(m.LParam);
            return;
        }

        if (m.Msg == NativeMethods.WM_MOVING)
        {
            var currentScreenPoint = Cursor.Position;
            var proposed = new Rectangle(
                DragStartBody.X + (currentScreenPoint.X - LeftDragStartScreenPoint.X),
                DragStartBody.Y + (currentScreenPoint.Y - LeftDragStartScreenPoint.Y),
                DragStartBody.Width, DragStartBody.Height);
            var body = ComputeMovedBody(proposed);
            // Re-decided against the proposed rect's own new position - a drag that crosses the
            // "would go off the top of the screen" threshold mid-tick flips right here, so
            // WriteBackWindowRect (next) already inflates using whichever side the button band
            // belongs on now, not wherever it was a moment ago. Checked for an actual change before
            // reassigning (not just reassigned unconditionally) because a plain move never fires
            // WM_SIZE on its own the way a resize does - nothing else would otherwise notice this
            // flip and repaint, leaving the button row (and, for FolderFenceForm, its own tab)
            // visibly painted on the wrong side/band until something unrelated finally repaints,
            // same "doesn't become visible for free" reasoning as _draggedSettingsButtonOnLeft below.
            var newButtonRowAtBottom = ComputeButtonRowAtBottomFor(body);
            if (newButtonRowAtBottom != ButtonRowAtBottom)
            {
                ButtonRowAtBottom = newButtonRowAtBottom;

                // RenderAndPresent (next) reads the window's own *actual* current size via
                // GetWindowRect - but at this point in a WM_MOVING handler, Windows hasn't actually
                // resized/repositioned the window yet (that only happens once WriteBackWindowRect's
                // own RECT is applied, after this handler returns). Left alone, RenderAndPresent
                // would render against TopBand/BottomBand's new (just-flipped) values but the
                // window's own *old* bounds - a one-frame mismatch that clipped the widget's own top
                // edge right at the flip. Applying the identical rect WriteBackWindowRect computes
                // via a synchronous SetWindowPos first (same formula, same already-flipped
                // OuterMargin/TopBand/BottomBand) makes GetWindowRect - and so RenderAndPresent - see
                // the correct, already-resized bounds; WriteBackWindowRect below just reapplies the
                // same rect for WM_MOVING's own protocol, a no-op by the time it runs.
                NativeMethods.SetWindowPos(Handle, IntPtr.Zero, body.Left - OuterMargin, body.Top - TopBand,
                    body.Width + OuterMargin * 2, body.Height + TopBand + BottomBand,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                RenderAndPresent();
            }
            WriteBackWindowRect(m.LParam, body);
            m.Result = (IntPtr)1;

            // See _draggedSettingsButtonOnLeft's own comment - this is the one piece of a live move
            // that doesn't otherwise become visible for free, so it gets its own explicit (but
            // change-gated) repaint.
            if (ShowsButtons)
            {
                var onLeft = ShouldSettingsButtonOpenLeft(body.Width);
                if (onLeft != _draggedSettingsButtonOnLeft)
                {
                    _draggedSettingsButtonOnLeft = onLeft;
                    RenderAndPresent();
                }
            }
            return;
        }

        if (m.Msg == NativeMethods.WM_SIZING)
        {
            // Same fixed-anchor reasoning as WM_MOVING above, just per-edge: whichever edges this
            // particular resize handle doesn't control stay pinned exactly where the drag started
            // (DragStartBody, unchanging for the whole drag), and only the active ones move by the
            // cursor's total delta since then.
            var edges = SnapEdgesFromWmSz((int)m.WParam.ToInt64());
            var currentScreenPoint = Cursor.Position;
            var dx = currentScreenPoint.X - LeftDragStartScreenPoint.X;
            var dy = currentScreenPoint.Y - LeftDragStartScreenPoint.Y;
            var start = DragStartBody;
            var proposed = Rectangle.FromLTRB(
                (edges & SnapEdges.Left) != 0 ? start.Left + dx : start.Left,
                (edges & SnapEdges.Top) != 0 ? start.Top + dy : start.Top,
                (edges & SnapEdges.Right) != 0 ? start.Right + dx : start.Right,
                (edges & SnapEdges.Bottom) != 0 ? start.Bottom + dy : start.Bottom);
            var body = ComputeResizedBody(proposed, edges);
            ButtonRowAtBottom = ComputeButtonRowAtBottomFor(body);
            WriteBackWindowRect(m.LParam, body);
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == WM_NCLBUTTONDBLCLK)
        {
            // HitTest's own HTCAPTION covers the whole draggable margin/title row, but a
            // double-click should only trigger a rename over the title row itself - anywhere else in
            // this non-client area, do nothing rather than letting the default proc maximize the
            // window (its usual caption double-click behavior).
            Activation.Activate();
            if (IsOverTitleRow(m.LParam))
                BeginRename();
            return;
        }

        if (m.Msg == NativeMethods.WM_NCRBUTTONDOWN)
        {
            // A real caption's right-click would show the system menu via the default proc - there's
            // no such menu for this custom-drawn title row, so this always swallows the message
            // itself rather than falling through to base.WndProc/DefWindowProc. Only activates when
            // landing outside the title text itself - a right-click that's about to show the Rename
            // menu already gets its own feedback from that menu appearing, so it shouldn't also engage
            // the widget (show its chrome buttons) underneath at the same time; right-clicking
            // anywhere else in the margin/title row (no menu to show) still activates as before.
            if (IsOverTitleRow(m.LParam))
                ShowTitleContextMenu();
            else
                Activation.Activate();
            return;
        }

        if (m.Msg == WM_RBUTTONUP)
        {
            // The client-body counterpart to WM_NCRBUTTONDOWN above - a right-click anywhere on the
            // widget activates it, not just the margin/title row. Always swallowed here too (same
            // "DefWindowProc's own default handling never gets a chance to run" reasoning, including
            // the defensive Capture release - see WM_NCRBUTTONDOWN's own comment) rather than falling
            // through, so a subclass's own OnClientRightClick is the only thing that runs afterward.
            Capture = false;
            Activation.Activate();
            var l = m.LParam.ToInt64();
            var contentPoint = ToContent(new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)));
            OnClientRightClick(contentPoint);
            return;
        }

        if (m.Msg == NativeMethods.WM_CTLCOLOREDIT)
        {
            // Sent by a rename EditBox to its owner (GetParent resolves here even though it's a
            // top-level WS_POPUP, not a true child - see EditBox's own class comment) each time it
            // needs to know what to paint itself with.
            NativeMethods.SetTextColor(m.WParam, ColorRef(EditBoxTextColor));
            NativeMethods.SetBkColor(m.WParam, ColorRef(EditBoxBackgroundColor));
            m.Result = GetThemeBrush(EditBoxBackgroundColor);
            return;
        }

        if (m.Msg == NativeMethods.WM_NCLBUTTONDOWN)
        {
            // A left click on the title row activates the window - not returning early: the default
            // proc still needs this message to actually start the move/resize.
            var hitTestCode = (int)m.WParam.ToInt64();
            if (hitTestCode == HTCAPTION)
                Activation.Activate();
            OnNcLButtonDown(hitTestCode);
        }
        else if (m.Msg == NativeMethods.WM_NCMOUSEMOVE)
        {
            // WinForms' own client-area hover tracking (OnMouseEnter/OnMouseLeave) doesn't cover
            // this - the margin/resize band reports HTLEFT/HTCAPTION/etc. (see HitTest), so the OS
            // treats it as non-client and never raises the client mouse events those hook.
            // TrackMouseEvent needs re-arming on every WM_NCMOUSEMOVE (Windows disarms it after
            // firing once), not just the first - but only bother once per hover session since
            // _isNonClientHovered already being true means it's still armed from last time.
            if (!_isNonClientHovered)
            {
                _isNonClientHovered = true;
                RenderOpacity.BeginIfNeeded();
            }
            var tme = new TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = NativeMethods.TME_LEAVE | NativeMethods.TME_NONCLIENT,
                hwndTrack = Handle,
            };
            NativeMethods.TrackMouseEvent(ref tme);
        }
        else if (m.Msg == NativeMethods.WM_NCMOUSELEAVE)
        {
            _isNonClientHovered = false;
            RenderOpacity.BeginIfNeeded();
        }

        base.WndProc(ref m);

        switch (m.Msg)
        {
            case WM_SIZE:
                RenderAndPresent();
                OnResized();
                break;

            case WM_ENTERSIZEMOVE:
                IsMoving = true;
                DragStartBody = GetCurrentBody();
                LeftDragStartScreenPoint = Cursor.Position;
                _draggedSettingsButtonOnLeft = ShouldSettingsButtonOpenLeft(DragStartBody.Width);
                BeginSnapDrag();
                RenderOpacity.BeginIfNeeded();
                break;

            case WM_EXITSIZEMOVE:
                Fences.SnapLines.EndDrag();
                OnDragEnd();
                IsMoving = false;
                IsResizing = false;
                RenderOpacity.BeginIfNeeded();
                // A pure move (no resize) never otherwise triggers a repaint - WM_SIZE above already
                // covers the resize case - but the Settings button's own corner (see
                // ShouldSettingsButtonOpenLeft) depends on this widget's absolute screen position, so
                // a move that crosses the point where the button should flip corners would otherwise
                // leave the stale side drawn (and hit-tested wrongly, since HitTest recomputes fresh
                // on the next click) until some unrelated repaint happened to notice. Only worth doing
                // while the button's actually showing.
                if (ShowsButtons)
                    RenderAndPresent();
                break;
        }
    }
}
