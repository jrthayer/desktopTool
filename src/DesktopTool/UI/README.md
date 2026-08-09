# UI

Shared chrome, theming, and hand-painted controls used by every on-screen element in Desktop
Tool — not a feature of its own, but the common foundation [Fences](../Features/Fences/README.md)
and [Layouts](../Features/Layouts/README.md) are both built on. Nothing here is hidden desktop
"content" the way a fence's icon grid or a layout's row list is; it's the reusable move/resize/
snap/rename/settings/theme/list machinery, plus a handful of small dark-themed controls, that a
feature's own UI code draws on instead of re-solving.

## LayeredWidgetForm

[`LayeredWidgetForm`](LayeredWidgetForm.cs) is the base class behind every draggable on-screen
widget — [`FenceForm`](../Features/Fences/UI/FenceForm.cs) and
[`LayoutLauncherWidget`](../Features/Layouts/UI/LayoutLauncherWidget.cs) both derive from it. It's a
hand-painted, layered Win32 window (`WS_POPUP` + `WS_EX_LAYERED`, no WinForms child controls,
presented via [`LayeredWindowPresenter`](../Native/LayeredWindowPresenter.cs) so rounded corners and
per-pixel opacity render smoothly instead of through a hard-edged `SetWindowRgn` mask) that gets the
following for free, with no subclass code beyond a handful of small hooks (what the title text is,
what a few theme colors are, what rows its own settings dropdown adds):

- **Move/resize** via the OS's own interactive move/resize loop (`WM_ENTERSIZEMOVE`/
  `WM_SIZING`/`WM_EXITSIZEMOVE`), with a subclass only supplying its own hit-testing and geometry
  hooks (`SupportsResize`/`ResizableEdges`/`ComputeMovedBody`/`ComputeResizedBody`).
- **Snapping** — every drag snaps against every other live widget's edges (gathered generically
  across every `LayeredWidgetForm` on screen, not just fences of the same type) and custom snap
  lines, via the shared [`SnapEngine`](../Features/Snapping/README.md), the same way for any
  subclass.
- **Rename** — a title-row double-click or right-click > Rename opens an [`EditBox`](EditBox.cs)
  in place; Enter commits, Escape cancels.
- **The Settings button and its dropdown** — a band above or below the widget (flipped to whichever
  side actually fits the monitor) hosts a Settings button that opens a [`DropdownMenu`](DropdownMenu.cs)
  built from `BuildSettingsRows`. The base's own default rows (see `BuildBaseSettingsRows`) cover
  every [`IWidgetStyle`](IWidgetStyle.cs) knob — title font size/alignment, a header close button,
  Hide Header, Full Opacity When Active, Header Border Mode, the color grid, Header Darkness/Opacity/
  Tint Strength, Corner Radius, Margin — under a **Base** flyout; a subclass with its own extra
  settings (a fence's Hide
  Shortcut Names/OCD Sizing) adds them via `BuildAdditionalSettingsRows`, shown in their own
  **Additional** flyout instead of having to rebuild the whole row list. See
  [Fences: Fence settings](../Features/Fences/README.md#fence-settings) for what every row actually
  does from a user's perspective.
- **Theme derivation** — body/title/border/field colors are all derived from the subclass's live
  `Style` (an `IWidgetStyle`) plus [`StyleTint`](StyleTint.cs)'s shared tint/darken/lighten math, so
  every widget on this base looks and themes identically.
- **A generic scrollable row list** — `GetListArea`/`ListRowCount`/`PaintListRow`/`ListScrollOffset`
  let a subclass (Layout Launcher's saved-layout list) get scrollbar geometry, thumb-drag, and
  mouse-wheel handling from the shared [`Scrollbar`](Scrollbar.cs) class for free; what a row
  actually shows and does on click is entirely the subclass's own business.
- **Chrome and content buttons** — `ChromeButton`s chain outward from the Settings button in the
  margin band (only shown while activated, like Settings itself — a fence's Duplicate/Delete, Layout
  Launcher's Close); `ContentButton`s sit inside the widget's own visible body instead (Layout
  Launcher's Manage Layouts.../Save Current Layout), always visible regardless of activation state.
  Both share the same arm-on-mouse-down/fire-on-matching-mouse-up pattern.
- **Activation** ([`WidgetActivation`](WidgetActivation.cs)) — engagement chrome (the Settings
  button, the bright active-state border) only shows while a widget is actively engaged (right-click
  anywhere, or a title-bar click) or has a menu of its own open, not just because it happens to have
  OS focus.
- **Opacity easing** ([`OpacityAnimator`](OpacityAnimator.cs)) — Full Opacity When Active eases the
  render opacity toward its target over several ticks rather than jumping there in one repaint,
  since a layered window's own bitmap-push presentation has no equivalent of a plain
  `Form.Opacity` to animate.

## Style contract

[`IWidgetStyle`](IWidgetStyle.cs) is the data contract (`TintColor`, `HeaderDarkness`, `Opacity`,
`FullOpacityOnHover`, `TintStrength`, `Margin`, `CornerRadius`, `TitleFontSize`, `TitleAlignment`,
`HeaderBorderMode`, `LightBorder`) that anything implementing it gets `LayeredWidgetForm`'s shared
theme derivation and settings-dropdown rows from, without re-declaring them. Every current widget
model (`FenceModel`, `LayoutLauncherModel`, `WidgetManagerModel`) implements it the same way — by
inheriting [`WidgetStyleModel`](WidgetStyleModel.cs), a plain abstract base that holds every one of
those properties (plus `HideHeader`/`HeaderCloseButton`, which aren't technically part of
`IWidgetStyle` but were still identical across all three) so a fourth widget wanting the same styling
only has to inherit it
instead of retyping a dozen properties again. Position/size/title/visibility stay out of that base
on purpose — see its own class comment for why `FenceModel`'s shape there genuinely differs from the
other two rather than just happening to be a different copy of the same thing.
[`StyleMenuRows`](StyleMenuRows.cs) builds the actual dropdown rows (the color grid plus the Header
Darkness/Opacity/Tint Strength sliders and Corner Radius/Margin steppers) against the `IWidgetStyle`
contract, and [`StyleTint`](StyleTint.cs) supplies the underlying blend math (the eight preset
colors, tinting a base color toward a pick, darkening toward black for a header band, lightening
toward white for a raised panel).

## DropdownMenu

[`DropdownMenu`](DropdownMenu.cs) is a persistent, dark-themed popup `Form` that replaces a native
`TrackPopupMenuEx` menu — a real Win32 popup closes itself the instant any item is clicked, which
made flipping several checkboxes in a row require reopening it every time. This one stays open until
it loses activation, supports nested flyout submenus, and its rows aren't limited to plain
commands/checkboxes — a row can be a slider, a `-`/`+` stepper, a Left/Center/Right alignment picker,
or a swatch in a color grid, each hand-drawn and interactive in place. It's what both the Settings
dropdown and [`ComboButton`](ComboButton.cs)'s popup are built from.

## Small shared controls

- [`AppTheme`](AppTheme.cs) — the app-wide dark palette (body/field/border/hover/accent/text colors,
  the shared font) for chrome that isn't tied to any one widget's own live tint.
- [`DarkButton`](DarkButton.cs) — a `Button` subclass that only overrides painting for the disabled
  case, working around WinForms always substituting a fixed system color for a disabled button's
  label regardless of `ForeColor`.
- [`ComboButton`](ComboButton.cs) — a closed-state combo-box face that opens a `DropdownMenu` instead
  of a native combo popup, since a plain `ComboBox`'s dropdown is rendered by the OS with baked-in
  light-theme visual styles no color override reaches.
- [`EditBox`](EditBox.cs) — a thin wrapper around a native Win32 Edit control, used for rename boxes
  on a layered window that has no WinForms `Controls` collection of its own to host a real `TextBox`
  in.
- [`Scrollbar`](Scrollbar.cs) — the thumb-drag/track-paging/wheel-scroll geometry and interaction
  shared between a fence's own icon grid and `LayeredWidgetForm`'s generic row list.
- [`PaintedTooltip`](PaintedTooltip.cs) — a small hand-painted tooltip pill, painted directly into a
  caller's own bitmap rather than a separate popup window, so it can never extend past the bounds
  it's given (unlike a native `ToolTip`, which also fights this app's dark theme — see its own class
  comment).
- [`RoundedRectPath`](RoundedRectPath.cs) — rounded-corner GDI+ paths for a widget's own body/title
  fills (full rounding, or top-corners-only for a header band sitting flush above a rounded body).
- [`WarningIcon`](WarningIcon.cs) — a hand-drawn caution-triangle glyph (Layout Launcher's row error
  badge, Manage Layouts' own missing-monitor warning), drawn with plain GDI+ instead of the Unicode
  "⚠" character, which rendered as a garbled/missing-glyph box in some fonts.
- [`EyedropperOverlay`](EyedropperOverlay.cs) — a full-virtual-screen, click-through-looking overlay
  for the Settings menu's Eyedropper color pick, letting a color be sampled from anywhere on screen,
  not just inside this app's own windows.
- [`TrayMenuRenderer`](TrayMenuRenderer.cs) — a `ToolStripRenderer` that paints a `ContextMenuStrip`
  (the tray icon's own menu, and any native rename context menu) in the same dark palette as
  everything else, since a `ToolStripDropDownMenu`'s items are already routed through this exact
  renderer hook rather than baked-in visual-styles chrome.
