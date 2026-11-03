# Widget Manager

A persistent on-screen panel listing the app's three toggleable widgets - Fences, Layout Launcher,
and Snap Lines - each with an on/off switch plus a row-specific action button. It's the entry point
into most of what used to be separate tray items - creating a fence, editing snap lines, and
showing/hiding Layout Launcher all live on its own rows now instead. All of this feature's code
lives under this folder - `WidgetManager/` for the model/store, `WidgetManager/UI/` for the widget
itself. It's built on the same [`LayeredWidgetForm`](../../UI/LayeredWidgetForm.cs) base as a fence
and the [Layout Launcher](../Layouts/README.md#layout-launcher-widget) widget, so it shares their
move/resize/snap/rename/Settings-menu/theming for free - see [UI](../../UI/README.md) for that
shared foundation. Its own entry point - a top-level **Widget Manager** tray item, not nested under
anything - lives in the app-wide [`TrayApplicationContext`](../../TrayApplicationContext.cs) - see
the base [README](../../../../README.md) for the rest of the tray menu.

## Rows

Widget Manager's three rows are fixed - nothing adds or removes one, unlike the Layout Launcher's
own saved-layout list. Each row has a label, an on/off switch (a small hand-drawn pill, since
there's no toggle-switch control elsewhere in the app to reuse), and its own action button:

- **Fences** - the switch shows/hides every fence and folder fence at once (reads/flips
  `FenceManager.AnyVisible`/`FolderFenceManager.AnyVisible`, the live state, so it can never drift
  out of sync with a fence hidden/shown some other way). **Add Fence** creates a new, empty fence.
- **Layout Launcher** - the switch shows/hides that widget (reads/flips its own `Visible`).
  **Edit Layouts** opens **Manage Layouts**, the same as that widget's own button.
- **Snap Lines** - the switch turns every custom snap line (including the seeded default edge
  lines - they're plain entries in the same list, not distinguished from a user-drawn one) off or on
  as a drag candidate app-wide, while fence-to-fence edge snapping keeps working either way - see
  [Fences: Snap lines](../Fences/README.md#snap-lines). **Edit** opens **Manage Snap Lines...** -
  editing lines works regardless of the switch's state.
- **Wallpapers** - the switch shows/hides the [Wallpapers](../Wallpapers/README.md) widget (reads/flips
  its own `Visible`). No action button. Hiding the widget doesn't stop slideshows; they keep running.
- **Junk Drawer** - the switch shows/hides the [Junk Drawer](../JunkDrawer/README.md) widget (reads/flips
  its own `Visible`). No action button.

The Fences switch also updates by itself whenever fences are shown or hidden in bulk
(`FenceManager.VisibilityChanged`/`FolderFenceManager.VisibilityChanged`). The Junk Drawer's Hide
Desktop is separate: it hides every widget, Widget Manager included, without changing this switch.

## Settings

Beyond the Base flyout every widget on this base shares (see [UI](../../UI/README.md)'s own Style
contract section), Widget Manager's Settings menu has an **Additional** flyout with **Rows Shown**
and **Always Max Rows**. **Start with Windows** and **Show Hidden Files** used to be here too; they've
moved to the [Junk Drawer](../JunkDrawer/README.md) widget (and the tray menu still has them).

## Limitations

Snap Lines had no on/off concept at all before this widget existed - custom lines were always live
snap candidates, only ever suppressed for a single drag by holding the right mouse button. The new
`SnapLineManager.Enabled` flag this row's switch controls is a genuinely new, persisted, app-wide
setting (`snaplines.json`), not just a fresh way to reach something that already existed.
