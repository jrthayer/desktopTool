# Junk Drawer

The **Junk Drawer** widget collects small system-level tools that don't belong to any one feature,
so they're one click away on the desktop instead of buried in the tray menu.

## Switches

Each is a row with an on/off switch; click anywhere on the row to flip it.

| Switch | What it does |
|---|---|
| **Show Hidden Files** | Windows' own "Show hidden files, folders, and drives" setting. Takes effect straight away on the desktop and in open File Explorer windows (see `HiddenFilesManager`). The tray menu has a copy. |
| **Start with Windows** | Adds or removes Desktop Tool from your user's Run key, so it starts when you sign in. The tray menu has a copy. |
| **Hide with Double Click** | While on, double-clicking an empty part of the desktop hides the desktop (see below), and double-clicking it again brings it back. Off by default. |

## Hide Desktop

The **Hide Desktop** button at the bottom hides every Desktop Tool widget at once: fences, folder
fences, Layout Launcher, Wallpapers, Widget Manager, and the Junk Drawer itself. To bring them back,
use **Show Desktop** in the tray menu, double-click the tray icon, or (with Hide with Double Click on)
double-click the desktop.

- Only widgets that were showing get hidden, and only those come back. A widget you'd closed stays
  closed.
- No widget's own visibility setting changes, and the hidden state isn't saved. Restarting Desktop
  Tool always brings everything back.
- It all goes through `UI/DesktopHider`.

## How the desktop double-click works

Clicks on the desktop go to Explorer, not Desktop Tool, so `DesktopDoubleClickWatcher` watches them
with a low-level mouse hook. The hook is only installed while Hide with Double Click is on. Two
clicks within Windows' double-click time and distance count as a double-click. It only counts when
it lands on **empty** desktop:
- not on a fence or any other window (checked with `WindowFromPoint`);
- not on a desktop icon (checked with an `LVM_HITTEST` on Explorer's icon list - see
  `DesktopListView.IsEmptyDesktopAt`).

So double-clicking an icon still opens it, and double-clicking inside a fence behaves as usual. The
clicks still reach Explorer either way; nothing is swallowed.

## Staying in sync

Show Hidden Files and Start with Windows can be changed from here or the tray menu. Each change
raises an event (`HiddenFilesManager.Changed`, `StartupManager.Changed`) that the widget listens to,
so it repaints straight away. Changes made outside the app (Explorer's Folder Options, Task
Manager's Startup tab) have no event to listen to, so the widget re-reads every switch when the
mouse moves over it.

## Adding a switch

Switches are a small list in `JunkDrawerWidget`'s constructor: a label, a function that says whether
it's on, and an action that flips it. Adding one is one more line there; the widget's default height
follows the number of switches.

## Limitations

- "Start with Windows" only checks the Run key. If Desktop Tool was disabled from Task Manager's
  Startup tab, the switch can show on while Windows won't actually start it (see `StartupManager`).
- Hide with Double Click needs Desktop Tool to read Explorer's icon list. If it can't (for example
  when Desktop Tool and Explorer run at different permission levels), double-clicks are ignored.
