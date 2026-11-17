# Window Snapping

Edge snapping for every normal app window on the desktop, not just this app's own widgets. It's
switched on and off by the **Window Snapping** row in [Widget Manager](../WidgetManager/README.md).
The setting is saved to `window-snapping.json` and is on by default. The
[Layouts](../Layouts/README.md) tools will move into this feature later.

## What it does

- **Snap on release.** Finish moving or resizing a window with one of its edges within 16px of a
  neighbouring window's edge, and it snaps flush to it. A move snaps the whole window. A resize only
  snaps the edges you were dragging. An edge only snaps to windows it actually faces: a window at
  the top of the screen can't pull on the left edge of one at the bottom.
- **Back on screen on release.** Let go with part of the window off the screen (or under the
  taskbar), and it's brought back inside that monitor's working area. The monitor is the one holding
  most of the window.
  - **Moved windows** slide back to the nearest screen edge and keep their size. A window bigger
    than the screen lines up with the left or top edge and the rest is trimmed.
  - **A side that snapped to a neighbour** isn't slid. Sliding would pull it off the neighbour, so
    that side is trimmed instead and the snapped edge stays put. A window snapped left-to-right can
    still slide up out from under the taskbar.
  - **Resized windows** have the edges that hang off trimmed. Edges already on screen stay where
    they are. A window almost entirely off the screen (less than 100px would be left) is left alone.
- **Linked resize.** Drag an edge that sits flush with other windows' edges, and theirs follow,
  the way Windows Snap does for snapped windows. In a 2×2 grid, dragging the centre line of any one
  window moves it through all four. Followers are never squeezed below 100px.

Nothing is remembered between drags. Each drag looks at where every window is right then, so two
windows you pulled apart and dropped back next to each other are linked again straight away.

Windows' own shared-edge resize only works for windows it considers *snapped*, and no public API
snaps a window to an arbitrary rect. So [`WindowSnapper`](Native/WindowSnapper.cs) does it itself,
by moving the other windows with `SetWindowPos` while you drag.

## Windows' own snapping

While Window Snapping is on, two Windows behaviors that fight it are turned off
([`WindowsSnapOverride`](Native/WindowsSnapOverride.cs)):

- **Stretch to full height.** Normally, resizing a window's top or bottom edge to the edge of the
  screen stretches it to full height. That throws away the size you dragged to and breaks a shared
  top edge.
- **Drag-to-edge docking.** Normally, dragging a window to the top edge maximizes it, and dragging
  it to the left/right edge half-snaps it. Windows has no switch for the top edge alone, so both go.

Win+Arrow and Snap Layouts keep working.

Your own settings come back when you turn the switch off or quit the app. The change is never saved
to the registry, and restoring reads your saved settings from there. So after a crash, they come back
the next time the app quits normally or you sign in. If one of your saved settings is already off,
it stays off.

## Which windows count

Visible, top-level, unowned windows with a caption or resizable frame, in their normal state.
These are skipped:

- Minimized and maximized windows.
- Windows on other virtual desktops (cloaked).
- The desktop, the taskbars, and Start/Search/Action Center flyouts.
- This app's own fences and widgets, which have their own [snapping](../Snapping/README.md).
- Any window completely hidden behind another single window, so you never snap to an edge you
  can't see.

All edges are measured on the *visible* frame (DWM's extended frame bounds), not `GetWindowRect`.
`GetWindowRect` includes Windows 10/11's invisible ~7px resize borders, which would leave a gap.

## Cost

While the switch is on, only the move/size start/end WinEvent is hooked. It fires twice per drag of
any window. A location-change hook exists only while a window is being dragged. It sees every
window's moves, but anything that isn't the dragged window or one of its followers is ignored with
a single lookup. While the switch is off, nothing is hooked.

## Keeping followers in step

Followers are resized with an *asynchronous* `SetWindowPos`, which posts the request to the other
app instead of waiting for it. A synchronous one would freeze this app whenever a follower hangs.
The dragged edge moves every few milliseconds. A heavy app like a browser or editor can't
re-layout that fast, so sending every position queued up stale sizes. The app then played them all
back in order, falling behind and catching up after you let go.

[`FollowRequest`](Native/FollowRequest.cs) keeps at most one request in flight per follower. The
next one is sent when that follower reports it has moved, or after 100ms if it never does. It's
always the newest position, skipping everything in between. On release, every follower is sent its
final position. A heavy app may move in visible steps, but it stays with the edge. Hooks are out-of-context, so no DLL is
injected into other apps.

## Limitations

- Snapping happens when you let go, not live during the drag. Windows runs the drag loop inside the
  other app's process, and pulling the window around mid-drag from outside fights with it.
- Apps that enforce their own minimum size may stop a follower short of the shared line.
