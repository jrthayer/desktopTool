# Desktop Tool

A Windows desktop app, run from the system tray, built to grow into a
general desktop-organization toolkit over time. Its features are
**Fences**, **Layouts**, and **Widget Manager**, built on a shared
**Snapping** engine — see below.

## Requirements

- .NET 8 SDK
- Windows 10/11

## Build & run

```
dotnet build DesktopTool.sln
dotnet run --project src/DesktopTool/DesktopTool.csproj
```

The app runs as a system tray icon only (no main window). Right-click the
tray icon for the menu.

## Features

### Fences

Draggable, resizable, translucent "fence" regions (Stardock Fences-style)
that group your desktop icons under a name you choose, with drag-and-drop
reordering/moving between fences, a synthetic Recycle Bin item, and
snap-to-fence/snap-to-guide-line dragging.

See [`src/DesktopTool/Features/Fences/README.md`](src/DesktopTool/Features/Fences/README.md)
for the full feature writeup, settings reference, and known limitations.
All of its code lives under [`src/DesktopTool/Features/Fences`](src/DesktopTool/Features/Fences).

### Layouts

Named profiles that relaunch (or reuse, if already running) a set of
programs and place each one's window on a chosen monitor and position.
**Save Current Layout** builds a profile straight from whatever's
currently open and where it's sitting, instead of picking each program by
hand through **Manage Layouts...**. Both live on the **Layout Launcher**
widget, which also lists every saved layout for one-click run. Toggled via
its own row on the [Widget Manager](#widget-manager) widget (tray menu >
Widget Manager).

See [`src/DesktopTool/Features/Layouts/README.md`](src/DesktopTool/Features/Layouts/README.md)
for the full feature writeup, the layout entry model, and known limitations.
All of its code lives under [`src/DesktopTool/Features/Layouts`](src/DesktopTool/Features/Layouts).

**Limitations:** Save Current Layout can end up including one of Desktop
Tool's own windows (a fence) as a captured entry - it's excluded by
matching the window's owning process ID against the running app's own, but
that hasn't reliably kept it out in every case seen so far. Harmless to
leave in a saved layout (running it just tries to "relaunch" Desktop Tool
itself, which is already running and a no-op in practice), but worth
deleting by hand via Manage Layouts if you notice it.

### Widget Manager

A persistent on-screen panel listing the app's three toggleable widgets -
Fences, Layout Launcher, and Snap Lines - each with an on/off switch plus
a row-specific action button (**Add Fence** / **Edit Layouts** / **Edit**),
reachable via the tray menu's own top-level **Widget Manager** item.

See [`src/DesktopTool/Features/WidgetManager/README.md`](src/DesktopTool/Features/WidgetManager/README.md)
for the full feature writeup and known limitations. All of its code lives
under [`src/DesktopTool/Features/WidgetManager`](src/DesktopTool/Features/WidgetManager).

### Wallpapers

The **Wallpapers** widget sets each monitor's desktop background separately: a
single image, a solid color, or a slideshow of a folder, each with its own
interval and shuffle. It shows a small map of your monitors in their real
arrangement; click one to change it. Toggled via its own row on the
[Widget Manager](#widget-manager) widget.

See [`src/DesktopTool/Features/Wallpapers/README.md`](src/DesktopTool/Features/Wallpapers/README.md)
for how each kind of background is applied and the known limitations. All of
its code lives under [`src/DesktopTool/Features/Wallpapers`](src/DesktopTool/Features/Wallpapers).

**Limitations:**
- Slideshows only change images while Desktop Tool is running. After you
  exit, the last image stays. On restart, an overdue monitor changes right
  away and the others wait out the rest of their interval.
- How images are fitted (Fill, Fit, Stretch, Center, Tile) is one Windows
  setting shared by every monitor. Span isn't offered, because it would
  override every per-monitor choice.
- Windows can't give each monitor its own solid color, so a solid color is
  saved as an image of that color at the monitor's resolution (in
  `%APPDATA%\DesktopTool\Wallpapers\`) and set as that monitor's wallpaper.
- Choosing a background here replaces any Windows slideshow or Windows
  Spotlight background.
- Changing a monitor's wallpaper in Windows Settings doesn't stop a slideshow
  running on it; the slideshow's next change replaces your pick.
- If a slideshow folder is moved or emptied, that monitor's tile shows a
  warning icon, and the slideshow tries again once per interval.

### Snapping

The pure edge-snapping geometry (`SnapEngine`) that gives both Fences and
the Layout Launcher widget their drag-to-snap feel — a shared, stateless
utility rather than a user-facing feature of its own.

See [`src/DesktopTool/Features/Snapping/README.md`](src/DesktopTool/Features/Snapping/README.md)
for what it does and how the two features above use it. All of its code
lives under [`src/DesktopTool/Features/Snapping`](src/DesktopTool/Features/Snapping).

## Shared UI foundation

Every draggable on-screen widget (a fence, the Layout Launcher) is built on
the same [`LayeredWidgetForm`](src/DesktopTool/UI/LayeredWidgetForm.cs) base
class - move/resize/snap, rename, a Settings menu, and theming all work the
same way for both rather than each feature reinventing them.

See [`src/DesktopTool/UI/README.md`](src/DesktopTool/UI/README.md) for that
base class and the rest of the shared dark-themed controls it's built from.
All of its code lives under [`src/DesktopTool/UI`](src/DesktopTool/UI).

## Tray menu

- **Widget Manager** — at the top, since it's the entry point into most of
  what used to be separate tray items (see [Widget Manager](#widget-manager)
  above) — creating a fence, editing snap lines, and showing/hiding the
  Layout Launcher all live on its own rows instead now. Toggles the Widget
  Manager widget itself.
- **Show/Hide All** — toggles every fence's visibility at once; also
  triggered by double-clicking the tray icon.
- **Start with Windows** — adds (or removes) Desktop Tool from your user's
  Run key so it launches automatically at sign-in. The checkbox always
  reflects the Run key's actual current state, even if changed by hand.
- **Show Hidden Files** — toggles Windows' own "Show hidden files, folders,
  and drives" Explorer setting (the same one under Folder Options), exposed
  here for convenience since fenced items live in a hidden `hiddenDesktop`
  folder (see [Fences: Desktop icon hiding](src/DesktopTool/Features/Fences/README.md#desktop-icon-hiding)).
  This is a system-wide setting, not something scoped to Desktop Tool -
  turning it on reveals every hidden file on your machine, not just fenced
  ones, and the checkbox always reflects its actual current state even if
  changed from Explorer's own Folder Options instead.
- **Add Recycle Bin** — adds the synthetic Recycle Bin fence item (see
  [Fences: Recycle Bin](src/DesktopTool/Features/Fences/README.md#recycle-bin)) to a
  new, dedicated fence. Hidden once one already exists anywhere, since only
  one is allowed.

Toggling **Show Hidden Files** takes effect on screen straight away: hidden
items appear or disappear on the desktop and in every already-open File
Explorer window, with no F5 needed. Writing the registry value alone
changes the setting without updating what's on screen, so the toggle also
uses the shell's own `SHGetSetSettings` call and the `WM_SETTINGCHANGE
"ShellState"` broadcast that Folder Options sends, then refreshes each open
Explorer window and the desktop's icon view (see `HiddenFilesManager`).
Earlier attempts that sent only `SHChangeNotify`, forced a repaint, or
posted the desktop's Refresh command on their own didn't work, because
Explorer was still using the old setting when it redrew.

**Limitations:** folder fences don't follow this setting; they always list
hidden files.
The setting is correct immediately either way - only the visual update is
delayed.
