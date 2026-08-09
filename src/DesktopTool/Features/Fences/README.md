# Fences

Desktop Tool's original feature: draggable, resizable, translucent "fence"
regions (Stardock Fences-style) that group desktop icons. All of this
feature's code lives under this folder — `Fences/` for the model/manager
layer, `Fences/UI/` for its windows and controls, `Fences/Native/` for the
Win32 interop it's built on. Entry points into it (Show/Hide All and Add
Recycle Bin in the tray menu; everything else - creating a fence, editing
snap lines - lives on [Widget Manager](../WidgetManager/README.md)'s own
rows instead) live in the app-wide [`TrayApplicationContext`](../../TrayApplicationContext.cs)
— see the base [README](../../../../README.md) for those.

## Fences

A fence is a draggable, resizable, translucent region that groups desktop
shortcuts under a name you choose. It owns a plain list of file paths
(`FenceModel.Files`) and draws its own icon+label grid for them rather than
showing the real desktop icons underneath. Dropping a file onto a fence adds
a reference to it; dragging an item within a fence reorders it; dragging an
item onto a *different* fence's window moves it there; dragging an item off
any fence entirely removes it from that fence (see Desktop icon hiding
below for what happens to its real desktop icon when that happens). While
an item is being dragged, a small pill under the cursor names what's about
to happen — "Move to *Fence Name*", "Change Position", "Remove from
Fence", or "Move to Recycle Bin" (see Recycle Bin below) — the same way
Windows itself hints at a drop target.

**Limitations:** a fence only remembers a file's path, not a live watch on
it - if the underlying file is later moved or deleted outside Desktop Tool,
its entry stays in the fence but its icon/label may go stale (see
`FenceForm.GetIcon`'s fallback handling) until removed by hand.

## Desktop icon hiding

When a shortcut is added to a fence, Desktop Tool hides its real desktop icon
so it isn't visible twice — once as the fence's own drawing of it, and once
underneath on the actual desktop. This works by moving the real file into a
hidden folder (`hiddenDesktop`) living directly on your own desktop -
Explorer's desktop view only shows items directly inside the merged
Desktop/Public Desktop root, not a subfolder's contents, so this makes the
item disappear the same way moving it anywhere else would, while keeping it
easy to find by hand (un-hide that one folder in Explorer) rather than
buried in an app-data folder. Two earlier approaches were tried and dropped
first: moving the icon's on-screen position off-screen (Explorer would
periodically undo that on its own, e.g. after full-screening another app on
a multi-monitor setup, for reasons this app has no reliable way to detect),
and setting the Hidden attribute on the file in place instead of moving it
(faster when it worked, but silently could never work at all for a file
whose own ACL blocks attribute access outright - observed on shortcuts
originally installed onto the Public Desktop by an elevated installer,
which kept that restrictive ACL even after being moved elsewhere).

**Limitations:** only applies to files that live directly in your (or the
Public) Desktop folder - anything dragged in from elsewhere never had a
real desktop icon to hide. If Desktop Tool is closed uncleanly (crash, Task
Manager kill) rather than via the tray's Exit, the file stays in
`hiddenDesktop` (fully intact and easy to find by hand) until Desktop Tool is
run again. Adding or removing a shortcut also visibly lags Explorer's
desktop icon view by roughly 1-2 seconds - the move itself, the shell
notification, and a forced repaint of the icon list were all confirmed
(via temporary timing instrumentation) to complete in single-digit
milliseconds, so this is happening inside Explorer's own rendering after
being told about the change, not anything on Desktop Tool's side. Accepted
as a known limitation rather than something worth chasing further; see the
base README's Tray menu section for the same issue in a different form
(Show Hidden Files).

## Recycle Bin

Desktop Tool can host a synthetic Recycle Bin icon inside a fence, since the
real desktop Recycle Bin is a virtual shell item with no filesystem path
and can't be dragged in directly. **Add Recycle Bin** in the tray menu
hides the real desktop icon (via the standard Explorer registry setting)
and creates a new fence containing the synthetic one in its place — it
always shows the real, state-aware (empty/full) system icon. Dropping a
file onto it — whether dragged fresh from Explorer or moved out of another
fence — sends that file to the real Recycle Bin (respecting your normal
delete-confirmation setting); double-clicking it opens the real Recycle
Bin folder. Removing the synthetic icon from its fence (dragging it out
onto the bare desktop) restores the real desktop icon's visibility. Only
one is allowed to exist across every fence at once.

**Limitations:** dragging a file directly out of the *real* Recycle Bin's
own folder view (rather than using Explorer's Restore command) and back
into a fence adds a reference to the file still sitting inside Recycle
Bin's own internal storage — deleting that fenced item again permanently
purges it instead of just re-deleting a normal file. Use Explorer's own
Restore first if you want a previously-deleted file back in a fence.

## Snap lines

While dragging a fence, it can snap to other fences' edges and to custom
guide lines you place yourself, the same gap-closing feel as Stardock
Fences' or a design tool's own alignment guides:

- **Left-click drag** snaps to both your custom snap lines and every other
  fence's edges by default. Holding the **right** mouse button down at the
  same time hides the fence-edge snapping for that drag, leaving just your
  custom lines active, if a screen full of fences is making snapping feel
  too eager.
- **Manage Snap Lines...** (Snap Lines' own **Edit** button on
  [Widget Manager](../WidgetManager/README.md)) opens an edit overlay: click and
  drag directly on a line to move it, or use the corner box to add a new
  one, set its orientation, position (typed in directly, or relative to a
  screen edge via the From Top/Bottom/Left/Right dropdown), and which
  monitor it belongs to. A line only offers itself as a snap target on the
  monitor it was created for.
- Every monitor gets four default lines — flush with its own top, bottom,
  left, and right working-area edges — seeded automatically the first time
  Desktop Tool sees it. They can be deleted like any other line, and won't
  come back once you do (a monitor is only ever seeded once).
- **Fence Margin** (see Fence settings below) adds a consistent gap instead
  of snapping flush to whatever it's snapping to.
- **Widget Manager**'s own Snap Lines switch (see
  [Widget Manager](../WidgetManager/README.md)) turns every custom/default
  line off or on as a drag candidate app-wide, without affecting
  fence-to-fence edge snapping - editing lines via Manage Snap Lines...
  still works either way.

The pure edge-snapping geometry itself (`SnapEngine`) lives as a sibling
feature — see [Snapping](../Snapping/README.md) — it has no dependency on
fences or any particular widget type, so any future draggable/resizable UI
element in this app can reuse it the same way `FenceForm` does.

## Fence settings

Click a fence to activate it, then click **Settings** near the top of its
title bar to open its settings menu. Two more buttons sit next to it: a
duplicate-icon button that creates a new, empty fence with this one's
settings (color, Hide Header/Labels, OCD sizing, size) copied over, and an
**x** that deletes the fence (with a confirmation prompt) — its shortcuts
aren't deleted, only removed from the fence; their real desktop icons are
restored (see Desktop icon hiding above).

The menu itself has two flyouts. **Base** holds every setting shared by any
widget built on [`LayeredWidgetForm`](../../UI/LayeredWidgetForm.cs) — a
fence and the [Layout Launcher](../Layouts/README.md#layout-launcher-widget)
widget both get the exact same rows here, so they're documented once
rather than per-widget:

- **Header > Font Size / Align** — the title text's own point size (7-14pt)
  and how it sits in its row (Left/Center/Right).
- **Header > Close Button** — off by default. When on, an always-visible
  "×" glyph sits in the title row itself, without needing to right-click or
  click the title to activate it first the way Settings does. On a fence
  specifically it triggers the same confirmed delete as the "×" Delete
  button above (a fence has no per-fence hide state of its own); on the
  Layout Launcher it hides the widget the same way its own Settings-menu
  Close does.
- **Hide Header** — hides the title bar entirely, reclaiming that space for
  the body. The widget can still be moved via its outer margin.
- **Full Opacity When Active** — off by default. When on, the widget
  renders fully opaque while hovered, while being dragged or resized, or
  while its own settings menu is open, easing back down to the Opacity
  slider's value once none of those still apply.
- **Header Border Mode** — borders every element the widget draws (its own
  outer border, its buttons, its list) in the header's own color instead of
  each one's usual border color, tying the whole widget together as one
  visibly matched set.
- **Color** — pick one of the eight preset swatches, **Custom...** for the
  full Windows color picker, **Eyedropper** to sample a color from anywhere
  on screen (even outside the app), or **Default** to reset to the plain
  dark theme.
  - **Header Darkness** — how much black blends into the title bar,
    independent of the widget's own color.
  - **Opacity** — how translucent the widget renders, clamped to a 15%
    floor so it can never be dragged all the way to invisible/unclickable.
  - **Tint Strength** — how strongly a preset/Custom... color blends into
    the dark theme rather than replacing it outright. An Eyedropper pick
    uses this the opposite way: 0% (where every fresh pick starts) keeps
    the sampled color exact, and dragging it up mutes that color back
    toward the plain theme instead. Picking any color — even re-picking
    the one already selected — resets Header Darkness, Opacity, and Tint
    Strength back to their defaults.
- **Corner Radius** — 0-50px. How rounded the widget's own body/title
  corners are; 0 is square.
- **Margin** — 0-100px (in steps of 5). How far this widget wants to sit
  from whatever it's snapping to (another fence's edge, or a custom snap
  line) while being dragged, instead of landing flush against it — see
  Snap lines above. It's this widget's own value that applies while it's
  the one being dragged, like a CSS margin.

**Additional** holds what's genuinely specific to a fence, on top of Base:

- **Hide Shortcut Names** — hides the label under each icon, showing icons
  only. Toggle it again to bring labels back.
- **OCD Fence Sizing** — after you resize the fence by hand, automatically
  snaps it to the tightest size that fits its icons (equivalent to running
  OCD → Both after every manual resize). Also fires immediately the moment
  you turn it on, rather than waiting for the next resize.
- **OCD** — a submenu with three one-off resize actions: **Both** (trims
  width and height), **Left/Right** (width only), and **Top/Down** (height
  only). Each shrinks or grows the fence to fit its current icons without
  changing its top-left corner.

**Limitations:** Full Opacity When Active's hover detection covers the
outer margin (used for dragging/resizing) as well as the visible body, but
it does so via a separate, lower-level Windows message path than normal
mouse events use — an edge case in a future Windows version changing that
behavior could in theory leave the margin's hover detection stale, though
the visible body would be unaffected either way.

## Credits

Fence contents (each fence owning its own list of file references and
rendering its own icon grid, rather than moving the real desktop icons
around) follows the approach used by
[NoFences](https://github.com/Twometer/NoFences), an open-source Stardock
Fences alternative. No code from that project is reused directly here, but
its design is what this app's drag-and-drop model is based on.
