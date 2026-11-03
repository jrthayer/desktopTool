# Fence Locker

The **Fence Locker** puts locks on fences and folder fences, so the programs and files in them
can't be used until a condition you chose is met. It's meant as a self-control tool: a pause and a
reminder of why you locked something, not security.

## Locks and loadouts

The **Fence Locker** widget (Widget Manager → Fence Locker) lists your **lock loadouts**: saved lock
setups you can place on as many fences as you like.

- **Add Lock** makes a new loadout. It starts with no ways in; give it a name, a reminder, and at
  least one way in, then save.
- **Click a loadout** to place it. The screen turns into a picker - every fence or folder fence you
  click gets a copy of that lock (one that's already locked asks for its current lock first). A
  banner at the top shows how many you've locked; press Esc, right-click, or click anything that
  isn't a fence to finish.
- **The cog** on a loadout edits or deletes it. That only changes the loadout - fences it was
  already placed on keep their own copy.
- **Manage Locks** lists the fences that are locked right now, with their status. Click one for:
  **Unlock** (just unlocks it for this session), **Lock** (when it's unlocked), **Change lock**,
  **Remove lock**, and its **Unlock history**. Changing or removing a lock asks for it first.
- **Executive Override...** (in Manage Locks) is the just-in-case way past a lock you can't meet.
  It turns the screen into a picker where only locked fences light up; each one you click asks
  why you're overriding it (it won't take an empty answer), then unlocks it for this session. Every
  override is logged as **Executive override** with your reason.

Each lock has:

- **Why are you locking it?** A note shown at the top of every unlock prompt, as a reminder.
- **Ways to open it.** Any one of them is enough:
  - **Time of day** - open between two times on the days you pick, for example weekdays
    18:00–22:00. An end time earlier than the start runs overnight (22:00–02:00).
  - **Special event** - opens once you type why you need it this time. The reason is saved.
  - **Password** - only a salted hash is stored, never the password itself.

A locked fence shows a padlock in its header (or its top-right corner, if the header is hidden):
closed while it's locked, open while a time window is open or it's been unlocked. **Click the
padlock** for the same menu as the fence's row in Manage Locks (without its unlock history):
**Unlock...** while it's locked - the way in for a fence with nothing in it to click - or **Lock**
while it's unlocked, plus **Change lock...** and **Remove lock...**.

## What a lock blocks

While locked, a fence asks to be unlocked instead of:
- **opening items** - double-click, Open, Show in Explorer, Open in Explorer, and browsing into a
  subfolder of a folder fence;
- **dragging items out**, to the desktop, Explorer, another fence, or the Recycle Bin;
- **editing it** - dropping items in, renaming or deleting items, renaming or deleting the fence,
  changing or clearing a folder fence's folder, copying a folder fence, or converting a fence to a
  folder fence;
- **changing the lock itself** - opening it from Manage Locks, or placing a different loadout on it,
  asks for the lock first.

Moving, resizing and restyling a locked fence still work.

## Unlocking

The prompt shows your reminder, then one section per way in: when its time window opens next, a
password box, and a "why do you need it now?" box. **Unlock** lets you in through whichever one you
filled in. A password or special-event unlock lasts **until Desktop Tool restarts**; **Lock** in
the fence's lock (from Manage Locks) ends it early. A time window just follows the clock.

## Unlock history

Every unlock is logged with its date, time, fence, how it was unlocked (Password, Special event,
Time window, or Executive override), and the reason for a special event or override. **Unlock History...** at the bottom of the widget
shows the whole log, newest first, and can be narrowed to one fence; each lock's settings also have
a **History...** button for just that fence. A time window is logged the first time the fence is
used in each window, not on every launch.

## Files

- `%APPDATA%\DesktopTool\fence-locks.json` - the lock on each fence.
- `%APPDATA%\DesktopTool\fence-lock-loadouts.json` - your saved loadouts.
- `%APPDATA%\DesktopTool\fence-unlock-log.json` - the unlock history.
- `%APPDATA%\DesktopTool\fence-locker.json` - the widget's own position and look.

## Adding a new kind of criterion

1. A class deriving from `Criteria/LockCriterion` (its `UnlockType`, `Describe`, and `IsSatisfied`
   if it can hold on its own, like a time window).
2. A `[JsonDerivedType]` line on `LockCriterion`, so it saves and loads.
3. An entry in `UI/CriterionUi.Kinds`: its name, editor, and prompt section.

The lock editor's "Add a way in" menu, the prompt, and the history all pick it up from there.

## Limitations

- **Not security.** Quitting Desktop Tool, opening the hidden `hiddenDesktop` folder, launching a
  program from the Start menu, or deleting `fence-locks.json` all get around a lock.
- Unlocks don't survive a restart, but neither do they expire on their own before one.
