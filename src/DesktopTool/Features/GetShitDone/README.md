# Get Shit Done

The **Get Shit Done** widget: a log of small daily commitments and how they actually went, kept so the
outcomes can be reviewed for patterns - what size of task gets done, at what time of day, on which
days, under how much load. It is an experiment log, not an enforcer: there are no streaks or scores,
a skipped commitment with a note about what got in the way is as useful a row as a finished one, and
a commitment that is never closed out is recorded as "no check-in" once its day has passed rather
than disappearing.

All of this feature's code lives under this folder - the log ([`CommitmentLog`](CommitmentLog.cs)),
the one-line parser ([`CommitmentParser`](CommitmentParser.cs)) and the review ([`Review`](Review.cs))
here, the widget itself under `UI/`. It's built on the same
[`LayeredWidgetForm`](../../UI/LayeredWidgetForm.cs) base as every other widget, and shown/hidden
from its own **Get Shit Done** row on [Widget Manager](../WidgetManager/README.md).

## How a day works

1. Commit to at most three things - the cap is what keeps the day's finish line reachable.
2. Close each one out as done, partly done, or skipped, with an optional note.
3. Once everything committed is done, the day is finished and the widget says so.

A day runs 4am to 4am, so closing something out after midnight still counts toward the day it was
promised for.

## Using it

- **Commit...** opens a text box over the bottom buttons. Type one line - `draft the intro 45m
  #writing`, the duration and `#tag` optional - and press Enter. Escape cancels.
- An open row has three buttons: check (done), half circle (partly done), cross (skipped). Partly
  done and skipped are recorded immediately and then open the text box for an optional note;
  Escape or clicking away leaves the close-out in place without one.
- A closed row shows the outcome it was closed with; clicking that glyph reopens it.
- Clicking any row's text opens its note for editing; hovering it shows the note.
- The line above the buttons is the day's status.
- **Review** opens the last 30 days' outcomes, split by size, time committed, weekday, load, and
  tag, in the same master/detail window the Readme uses. It only counts - a group with fewer than
  five entries is marked as too few to read into rather than given a percentage.

Settings > Additional has **Commitments Per Day** (1-5, default 3).

## Data

The commitments are in `%AppData%\DesktopTool\commitments.json`; the widget's own position and
styling are in `commitments-widget.json` beside it. Unlike every other store here, a log that can't
be read is copied to `commitments.json.corrupt` before anything is written over it. The widget
checks every 30 seconds for the day having rolled over, which is when anything left open from the
previous day becomes "no check-in".

## Tests

`tests/DesktopTool.Tests` covers the log, parser, and review (`dotnet test`). Desktop Tool has to be
closed first - the test build rebuilds the app, and a running copy locks its exe.

## Limitations

The text box is the same single-line [`EditBox`](../../UI/EditBox.cs) rename uses, so losing focus
commits whatever has been typed rather than discarding it. The review window doesn't scroll, so a
long section is cut off at the bottom of the pane until the window is resized taller.
