# Junk Drawer

The **Junk Drawer** widget collects small system-level tools that don't belong to any one feature,
so they're one click away on the desktop instead of buried in the tray menu. Each tool is a row with
an on/off switch; click anywhere on the row to flip it.

| Tool | What it does |
|---|---|
| **Show All Fences** | Shows or hides every fence and folder fence at once - the same as the tray's **Show/Hide All**. The switch is on while any of them is showing. |
| **Show Hidden Files** | Windows' own "Show hidden files, folders, and drives" setting. Takes effect straight away on the desktop and in open File Explorer windows (see `HiddenFilesManager`). |
| **Start with Windows** | Adds or removes Desktop Tool from your user's Run key, so it starts when you sign in. |

The tray menu keeps its own copy of all three. Widget Manager's **Junk Drawer** row shows or hides
the widget.

## Staying in sync

The same settings can be changed from several places: the tray menu, this widget, and Widget
Manager's Fences row. Every change made inside the app raises an event that the widgets listen to,
so they repaint straight away:
- `FenceManager.VisibilityChanged` / `FolderFenceManager.VisibilityChanged` - Show/Hide All.
- `HiddenFilesManager.Changed` - Show Hidden Files.
- `StartupManager.Changed` - Start with Windows.

Changes made outside the app (Explorer's Folder Options, Task Manager's Startup tab) have no event
to listen to, so the widget re-reads every switch when the mouse moves over it.

## Adding a tool

Tools are a small list in `JunkDrawerWidget`'s constructor: a label, a function that says whether
it's on, and an action that flips it. Adding one is one more line there; the widget's default height
follows the number of tools.

## Limitations

- "Start with Windows" only checks the Run key. If Desktop Tool was disabled from Task Manager's
  Startup tab, the switch can show on while Windows won't actually start it (see `StartupManager`).
