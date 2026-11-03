using DesktopTool.Features.FenceLocker.Criteria;
using DesktopTool.UI;

namespace DesktopTool.Features.FenceLocker.UI;

/// <summary>A criterion's section in the lock editor. Build turns what's typed into a criterion, or
/// returns null with error set when it isn't valid yet.</summary>
internal interface ICriterionEditor
{
    Control View { get; }
    LockCriterion? Build(out string? error);
}

/// <summary>The outcome of trying one criterion in the unlock prompt. Attempted is false when the
/// user left that criterion's input empty, so its error isn't worth showing.</summary>
internal sealed record UnlockAttempt(UnlockGrant? Grant, bool Attempted, string? Error)
{
    public static UnlockAttempt Skipped => new(null, false, null);
}

/// <summary>A criterion's section in the unlock prompt. TryUnlock is null for one with nothing to
/// enter (a time window just says when it opens).</summary>
internal sealed record UnlockInput(Control View, Func<UnlockAttempt>? TryUnlock);

/// <summary>Everything the UI needs to know about one kind of criterion.</summary>
internal sealed record CriterionKind(
    string DisplayName,
    Type Type,
    Func<LockCriterion> Create,
    Func<LockCriterion, ICriterionEditor> CreateEditor,
    Func<LockCriterion, DateTime, UnlockInput> CreateUnlockInput);

/// <summary>The registry of criterion kinds - the lock editor's "Add criterion" list and every
/// editor/prompt section come from here. A new criterion kind needs one entry in Kinds (plus its
/// class and [JsonDerivedType] line in Criteria/LockCriterion).</summary>
internal static class CriterionUi
{
    public static readonly IReadOnlyList<CriterionKind> Kinds = new[]
    {
        new CriterionKind("Time of day", typeof(TimeWindowCriterion),
            () => new TimeWindowCriterion(),
            c => new TimeWindowEditor((TimeWindowCriterion)c),
            (c, now) => TimeWindowPrompt((TimeWindowCriterion)c, now)),
        new CriterionKind("Special event", typeof(SpecialEventCriterion),
            () => new SpecialEventCriterion(),
            _ => new SpecialEventEditor(),
            (c, _) => SpecialEventPrompt((SpecialEventCriterion)c)),
        new CriterionKind("Password", typeof(PasswordCriterion),
            () => new PasswordCriterion(),
            c => new PasswordEditor((PasswordCriterion)c),
            (c, _) => PasswordPrompt((PasswordCriterion)c)),
    };

    public static CriterionKind KindOf(LockCriterion criterion) =>
        Kinds.First(k => k.Type == criterion.GetType());

    // ---- Shared themed controls ----

    public static Label MakeLabel(string text, bool dim = false, int maxWidth = 340) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(maxWidth, 0),
        ForeColor = dim ? AppTheme.DisabledText : AppTheme.Text,
        Margin = new Padding(0, 4, 0, 2),
    };

    public static TextBox MakeTextBox(int width, bool password = false, bool multiline = false) => new()
    {
        Width = width,
        Height = multiline ? 54 : 23,
        Multiline = multiline,
        UseSystemPasswordChar = password,
        BackColor = AppTheme.Field,
        ForeColor = AppTheme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 2, 0, 4),
    };

    public static FlowLayoutPanel MakeColumn() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = Padding.Empty,
    };

    public static DarkButton MakeButton(string text, int width = 90)
    {
        var button = new DarkButton { Text = text, Width = width, Height = 26, BackColor = AppTheme.Field, Margin = new Padding(0, 4, 8, 0) };
        AppTheme.StyleButton(button);
        return button;
    }

    // ---- Time of day ----

    private sealed class TimeWindowEditor : ICriterionEditor
    {
        private static readonly DayOfWeek[] Week =
            { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

        private readonly TextBox _start = MakeTextBox(60);
        private readonly TextBox _end = MakeTextBox(60);
        private readonly List<(DayOfWeek Day, CheckBox Box)> _days = new();

        public Control View { get; }

        public TimeWindowEditor(TimeWindowCriterion criterion)
        {
            _start.Text = criterion.Start.ToString("HH:mm");
            _end.Text = criterion.End.ToString("HH:mm");

            var times = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            times.Controls.Add(MakeLabel("Open from"));
            times.Controls.Add(_start);
            times.Controls.Add(MakeLabel("to"));
            times.Controls.Add(_end);
            times.Controls.Add(MakeLabel("(24-hour, e.g. 18:00)", dim: true));

            var days = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            foreach (var day in Week)
            {
                var box = new CheckBox
                {
                    Text = day.ToString()[..3],
                    AutoSize = true,
                    Checked = criterion.Days.Contains(day),
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = AppTheme.Text,
                    Margin = new Padding(0, 2, 6, 2),
                };
                _days.Add((day, box));
                days.Controls.Add(box);
            }

            var column = MakeColumn();
            column.Controls.Add(times);
            column.Controls.Add(days);
            column.Controls.Add(MakeLabel("An end time earlier than the start runs overnight.", dim: true));
            View = column;
        }

        public LockCriterion? Build(out string? error)
        {
            error = null;
            if (!TryParse(_start.Text, out var start) || !TryParse(_end.Text, out var end))
            {
                error = "Enter the times as HH:mm, for example 18:00.";
                return null;
            }
            var days = _days.Where(d => d.Box.Checked).Select(d => d.Day).ToList();
            if (days.Count == 0)
            {
                error = "Pick at least one day for the time window.";
                return null;
            }
            return new TimeWindowCriterion { Start = start, End = end, Days = days };
        }

        private static bool TryParse(string text, out TimeOnly time) =>
            TimeOnly.TryParseExact(text.Trim(), new[] { "H:mm", "HH:mm" }, null, System.Globalization.DateTimeStyles.None, out time);
    }

    private static UnlockInput TimeWindowPrompt(TimeWindowCriterion criterion, DateTime now)
    {
        var next = criterion.NextOpening(now);
        var text = next is { } opening
            ? $"Opens {FormatOpening(opening, now)} ({criterion.Describe()})."
            : $"Time window: {criterion.Describe()}.";
        return new UnlockInput(MakeLabel(text), null);
    }

    public static string FormatOpening(DateTime opening, DateTime now) =>
        opening.Date == now.Date ? $"today at {opening:HH:mm}"
        : opening.Date == now.Date.AddDays(1) ? $"tomorrow at {opening:HH:mm}"
        : $"{opening:dddd} at {opening:HH:mm}";

    // ---- Special event ----

    private sealed class SpecialEventEditor : ICriterionEditor
    {
        public Control View { get; } = MakeLabel(
            "Opens once you type why you need it this time. Every reason is saved in the unlock history.", dim: true);

        public LockCriterion? Build(out string? error)
        {
            error = null;
            return new SpecialEventCriterion();
        }
    }

    private static UnlockInput SpecialEventPrompt(SpecialEventCriterion criterion)
    {
        var box = MakeTextBox(340, multiline: true);
        var column = MakeColumn();
        column.Controls.Add(MakeLabel("Special event - why do you need it now?"));
        column.Controls.Add(box);
        return new UnlockInput(column, () =>
        {
            if (string.IsNullOrWhiteSpace(box.Text))
                return UnlockAttempt.Skipped;
            return new UnlockAttempt(new UnlockGrant(criterion.UnlockType, box.Text.Trim()), true, null);
        });
    }

    // ---- Password ----

    private sealed class PasswordEditor : ICriterionEditor
    {
        private readonly PasswordCriterion _existing;
        private readonly TextBox _password = MakeTextBox(200, password: true);
        private readonly TextBox _confirm = MakeTextBox(200, password: true);

        public Control View { get; }

        public PasswordEditor(PasswordCriterion existing)
        {
            _existing = existing;
            var column = MakeColumn();
            column.Controls.Add(MakeLabel(existing.HasPassword ? "New password (leave empty to keep the current one)" : "Password"));
            column.Controls.Add(_password);
            column.Controls.Add(MakeLabel("Type it again"));
            column.Controls.Add(_confirm);
            View = column;
        }

        public LockCriterion? Build(out string? error)
        {
            error = null;
            if (_password.Text.Length == 0 && _confirm.Text.Length == 0)
            {
                if (_existing.HasPassword)
                    return _existing;
                error = "Enter a password.";
                return null;
            }
            if (_password.Text != _confirm.Text)
            {
                error = "The two passwords don't match.";
                return null;
            }
            return PasswordCriterion.Create(_password.Text);
        }
    }

    private static UnlockInput PasswordPrompt(PasswordCriterion criterion)
    {
        var box = MakeTextBox(200, password: true);
        var column = MakeColumn();
        column.Controls.Add(MakeLabel("Password"));
        column.Controls.Add(box);
        return new UnlockInput(column, () =>
        {
            if (box.Text.Length == 0)
                return UnlockAttempt.Skipped;
            if (criterion.Verify(box.Text))
                return new UnlockAttempt(new UnlockGrant(criterion.UnlockType, null), true, null);
            box.SelectAll();
            return new UnlockAttempt(null, true, "That password isn't right.");
        });
    }
}
