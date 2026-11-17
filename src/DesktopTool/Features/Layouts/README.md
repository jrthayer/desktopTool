# Layouts

Named, saved profiles that relaunch (or reuse, if already running) a set of programs and place
each one's window on a chosen monitor and position. All of this feature's code lives under this
folder — `Layouts/` for the model/manager layer, `Layouts/UI/` for its windows, `Layouts/Native/`
for the Win32 interop it's built on. Entry points into it (the **Layout Launcher** widget toggle,
and wiring the editor/widget together) live in the app-wide
[`TrayApplicationContext`](../../TrayApplicationContext.cs) — see the base
[README](../../../../README.md) for the tray menu's own side of that.

## Layout profiles

A `LayoutProfile` is just a name plus a list of `LayoutEntry` objects
([`LayoutModel.cs`](LayoutModel.cs)); `LayoutManager` owns every saved profile and persists them
via `LayoutStore` (a plain JSON file under `%AppData%\DesktopTool`), the same relationship
`FenceManager` has to `FenceModel`/`FenceStore`. Unlike a fence, a profile has no live Form of its
own — there's nothing to show until it's actually run.

Each `LayoutEntry` records:

- **Program** — a `.exe`, a `.lnk`, or anything else `ShellExecute` can resolve, launched the same
  way `FenceForm.OpenItem` opens a fenced shortcut.
- **Target monitor** — stored as `Screen.DeviceName`, not an index or bounds, so it's more likely
  to survive a resolution change or the monitors being reordered in Windows' own display settings.
  Empty matches the primary screen.
- **Placement** — one of the fixed presets (Left/Right/Top/Bottom Half, the four quarters,
  Maximized) or `Custom`, an exact captured rect stored as 0–1 fractions of the target monitor's
  working area (not raw pixels), so a saved layout still makes sense after a resolution change or
  on a differently-sized monitor. Placement is always resolved against the monitor's *live* working
  area at run time (see `WindowPlacer.ResolveRect`), never a stored pixel rect.
- **Minimized** — independent of Placement, not an alternative to it: the window is placed normally
  first, then minimized on top of that, so its restore size/position is whatever Placement already
  resolved to.
- **URLs to open** (browser entries only) — one per line, all opened as separate tabs inside the
  same forced new window (`--new-window`) rather than one new window per URL.
- **Commands to run** (terminal entries only) — one per line, chained into a single argument string
  and left open afterward. For a `WindowsTerminal.exe` entry specifically, a **Shell** picker
  (PowerShell/PowerShell 7/Command Prompt) says which shell those commands should run in, since
  Windows Terminal isn't itself a shell and there's no way to tell from a captured window alone
  which one it was actually running.
- **Folder to open** (File Explorer entries only) — the folder the window reopens at, captured from
  the live window through `Shell.Application` ([`ExplorerWindows`](Native/ExplorerWindows.cs)) or
  picked with the editor's **...** button. Virtual folders (This PC, Home) are stored as their
  `::{GUID}` form and launched as `shell:::{GUID}`. Empty opens Explorer's default view. On run,
  only real folder windows (class `CabinetWClass`) count as candidates, so a taskbar or other shell
  window is never the one that gets placed.
- **Store/MSIX apps** (anything captured from `Program Files\WindowsApps\...`, Windows Terminal
  included) never launch from that captured path. The folder is version-stamped and disappears on
  every app update. Instead, `PackagedAppLauncher` looks up the package's current install and
  starts the app through its app execution alias (e.g. `wt.exe`) when it has one, otherwise by
  AUMID activation, so these entries survive updates.
- **Windows Terminal** launches additionally get `-w new`, so Terminal never tabs into an
  already-open window instead of opening the new window the layout needs to place.

**Limitations:** two entries for the same program still both become separate `LayoutEntry`
objects, but `WindowPlacer`'s claim-tracking keeps them from fighting over the same window on
replay — each entry's captured window title is preferred back over whichever one another entry
already claimed, falling back to "largest unclaimed window" when there's no title to go on (e.g.
two identical Notepad windows opened with no distinguishing title).

## Manage Layouts

**Manage Layouts...** (opened from the Layout Launcher widget) is where profiles and their entries
are actually edited ([`LayoutEditorForm`](UI/LayoutEditorForm.cs)):

- **Layouts** list on the left — create/rename/delete a profile.
- **Programs** list on the right — add an entry via **Select Window** (see below) or the browse
  button next to Program Path, remove one with its row's own "×" or the **Remove** button, and set
  its Monitor/Placement/Start-minimized fields. The URL or Commands group appears automatically
  once the entry's program resolves to a recognized browser or terminal exe.
- **Run** launches the selected profile immediately, without closing the editor, so a layout can be
  tested right after editing it.
- A caution icon appears next to a profile in the Layouts list, and a yellow banner above the
  Programs list, whenever it has an entry whose saved monitor is no longer connected and/or its
  last run left at least one program un-launched — both problems show at once, one line each.

## Capturing a layout

Building a profile entry-by-entry through the editor is one option; the other two capture
whatever's actually open and where it's actually sitting right now
(`WindowPlacer.CaptureCurrentLayout`/`CaptureWindow`), skipping this app's own windows, the
desktop and taskbars, shell chrome (Start menu, Search, Action Center, Widgets flyouts),
`ApplicationFrameHost.exe` (the frame Windows hosts UWP/Store apps in, which can't be relaunched on
its own), and cloaked windows that report as visible but aren't on screen, automatically. File Explorer folder windows *are* captured, along with the folder each is showing:

- **Save Current Layout** (on the Layout Launcher widget) captures *every* visible top-level window
  at once into a brand-new profile, then opens the editor jumped straight to it.
- **Select Window** (in the editor, for the currently-selected profile) captures one specific
  window — click it via a full-screen overlay — and adds it as a new entry.

A captured entry always stores the real `.exe` its owning process reported (resolved through a
`.lnk` via [`ShortcutResolver`](Native/ShortcutResolver.cs) when matching an already-launched
window back to its entry, since a shortcut's own file name routinely has nothing to do with its
target — e.g. `Google Chrome.lnk` → `chrome.exe`), and is captured `Maximized` if the window
already is (or, for a minimized window, was before it got minimized), `Custom` otherwise.

**Limitations:** Save Current Layout can end up including one of Desktop Tool's own windows (a
fence, or the Layout Launcher itself) as a captured entry — it's excluded by matching the window's
owning process ID against the running app's own, but that hasn't reliably kept it out in every case
seen so far. Harmless to leave in a saved layout (running it just tries to "relaunch" Desktop Tool
itself, which is already running and a no-op in practice), but worth deleting by hand via Manage
Layouts if you notice it.

With Windows 11's tabbed File Explorer, a window with several tabs is captured as just one folder
(the first tab `Shell.Application` lists for it), and replays as a single-tab window.

## Running a layout

`WindowPlacer.RunAsync` launches every entry up front (not one at a time, then waits), then polls
all of them together until each either shows up or a shared timeout passes — matching a freshly
launched window back to its entry by executable name, since Windows has no API to ask "which window
did the process I just launched create." Every entry always gets a fresh launch, never an
already-running window handed to it, so two entries for the same program never fight over which one
gets placed.

An entry that fails to launch, or whose window never appears in time, doesn't stop the rest of the
layout from running, but isn't silently skipped either — its program file name is collected and
surfaced through `LayoutManager.LaunchFailed` (a tray balloon notification) and
`GetLaunchError` (the caution icon on that layout's row, in both the Layout Launcher widget and the
editor's own Layouts list).

Each window is placed by its *visible* frame (DWM's extended frame bounds), not its
`GetWindowRect`. That rect includes Windows 10/11's invisible ~7px resize borders, so placing by it
would leave a visible gap between side-by-side windows. Capturing uses the visible frame too, so a
saved layout replays at exactly the same size.

## Linked edges

Windows a layout places flush against each other resize together and snap back together like any
other windows, through [Window Snapping](../WindowSnapping/README.md). Layouts don't link windows
themselves any more.

## Layout Launcher widget

The **Layout Launcher** (tray menu > Widgets > Layout Launcher) is a persistent, draggable/
resizable on-screen panel — built on the same [`LayeredWidgetForm`](../../UI/LayeredWidgetForm.cs)
base a fence is, so it shares move/resize/snap-to-fence/snap-to-guide-line dragging, rename, a
Settings menu, and theming with every fence on screen. It lists every saved layout for one-click
run, plus its own **Manage Layouts...** and **Save Current Layout** buttons pinned to the bottom of
its body.

- Clicking a row's name runs that layout; each row also has its own **Copy** (duplicate) and
  **Delete** (with confirmation) buttons at its right edge.
- A caution icon appears on a row whose layout's last run left at least one program un-launched;
  clicking it jumps straight to the editor for that profile instead of just naming the problem.
- **Rows Shown** (in the widget's Settings menu) sets how many rows the list reserves body space
  for — fewer saved layouts than that just leaves blank space below the list; more scrolls instead
  of growing further. **Always Max Rows** keeps it pinned to the current saved-layout count
  automatically instead of a fixed number. While that's ticked, **Min Height Fits All Rows** also
  appears. With it on, the widget can't be resized shorter than it takes to show every saved layout,
  and it grows when a new layout is saved. Deleting one lowers the minimum without shrinking the
  widget.
- Closing the widget (its "×") hides it rather than destroying it — its list, position, and every
  style setting persist to `layout-launcher.json` and come back on the next toggle-on or app
  restart, the same way a hidden fence's own state does.

## Credits

The snap-to-fence/snap-to-guide-line dragging and general chrome (move/resize/rename/Settings/
theme) the Layout Launcher widget shares with every fence is
[`LayeredWidgetForm`](../../UI/LayeredWidgetForm.cs)'s doing, not anything specific to this feature
— see the base README's Fences section for where that shared foundation lives.
