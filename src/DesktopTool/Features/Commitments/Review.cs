using System.Text;

namespace DesktopTool.Features.Commitments;

public sealed record ReviewSection(string Title, string Body);

/// <summary>
/// Turns the log into a handful of "the same outcomes, split by one condition" tables. Deliberately
/// only counts - no scores, streaks, or conclusions drawn for you - and a group with too few entries
/// to mean anything says so rather than showing a confident-looking percentage.
/// </summary>
public static class Review
{
    /// <summary>Below this many closed commitments a group's percentage is noise.</summary>
    public const int FewEntries = 5;

    public static IReadOnlyList<ReviewSection> Build(IEnumerable<Commitment> all, DateTime now, int days = 30)
    {
        var today = CommitmentLog.DayOf(now);
        var from = today.AddDays(-(days - 1));
        var closed = all
            .Where(c => c.Outcome != Outcome.Open && c.Day >= from && c.Day <= today)
            .OrderBy(c => c.CreatedAt)
            .ToList();

        if (closed.Count == 0)
            return new[] { new ReviewSection("Overview", $"Nothing closed out in the last {days} days yet.") };

        var byDay = closed.GroupBy(c => c.Day).ToList();
        var finishedDays = byDay.Count(g => g.All(c => c.Outcome == Outcome.Done));

        var overview = new StringBuilder();
        overview.AppendLine($"Last {days} days: {Line(closed)}");
        overview.AppendLine();
        overview.AppendLine($"Days with anything committed: {byDay.Count} of {days}");
        overview.Append($"Days where everything committed got done: {finishedDays} of {byDay.Count}");

        var sections = new List<ReviewSection>
        {
            new("Overview", overview.ToString()),
            new("By size", Table(closed, SizeBucket, SizeOrder)),
            new("By time committed", Table(closed, c => TimeBucket(c.CreatedAt.Hour), TimeOrder)),
            new("By weekday", Table(closed, c => c.Day.DayOfWeek.ToString(), WeekdayOrder)),
            new("By load", Table(closed, c => LoadBucket(byDay.First(g => g.Key == c.Day).Count()), LoadOrder)),
        };

        if (closed.Any(c => c.Tag is not null))
            sections.Add(new("By tag", Table(closed, c => c.Tag is { } tag ? "#" + tag : "(no tag)", null)));

        var noted = closed.Where(c => c.Note is not null && c.Outcome != Outcome.Done).ToList();
        if (noted.Count > 0)
        {
            var notes = new StringBuilder();
            // Newest first, and only the recent ones - this is read in a fixed-size pane.
            foreach (var c in Enumerable.Reverse(noted).Take(10))
                notes.AppendLine($"{c.Day:MMM d} - {c.Text} ({Name(c.Outcome)}): {c.Note}");
            sections.Add(new("What got in the way", notes.ToString().TrimEnd()));
        }

        return sections;
    }

    /// <summary>The same review as plain text, for the CLI.</summary>
    public static string Format(IReadOnlyList<ReviewSection> sections)
    {
        var text = new StringBuilder();
        foreach (var section in sections)
        {
            text.AppendLine(section.Title.ToUpperInvariant());
            text.AppendLine(section.Body);
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    private static readonly string[] SizeOrder = { "30m or less", "31-60m", "1-2h", "Over 2h", "No estimate" };
    private static readonly string[] TimeOrder = { "Morning (4am-noon)", "Afternoon (noon-5pm)", "Evening (5-10pm)", "Late (10pm-4am)" };
    private static readonly string[] WeekdayOrder = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
    private static readonly string[] LoadOrder = { "1 that day", "2 that day", "3 that day", "4+ that day" };

    private static string SizeBucket(Commitment c) => c.EstimateMinutes switch
    {
        null => SizeOrder[4],
        <= 30 => SizeOrder[0],
        <= 60 => SizeOrder[1],
        <= 120 => SizeOrder[2],
        _ => SizeOrder[3],
    };

    private static string TimeBucket(int hour) => hour switch
    {
        >= 4 and < 12 => TimeOrder[0],
        >= 12 and < 17 => TimeOrder[1],
        >= 17 and < 22 => TimeOrder[2],
        _ => TimeOrder[3],
    };

    private static string LoadBucket(int count) => LoadOrder[Math.Clamp(count, 1, 4) - 1];

    /// <summary>One line per non-empty group. order fixes the row order (and drops nothing - a group
    /// missing from it is appended); null sorts by group size instead.</summary>
    private static string Table(IReadOnlyList<Commitment> closed, Func<Commitment, string> key, string[]? order)
    {
        var groups = closed.GroupBy(key);
        var sorted = order is null
            ? groups.OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            : groups.OrderBy(g => Array.IndexOf(order, g.Key) is var i && i >= 0 ? i : int.MaxValue);

        var text = new StringBuilder();
        foreach (var group in sorted)
            text.AppendLine($"{group.Key}: {Line(group.ToList())}");
        return text.ToString().TrimEnd();
    }

    private static string Line(IReadOnlyList<Commitment> group)
    {
        var done = group.Count(c => c.Outcome == Outcome.Done);
        var line = new StringBuilder($"{done} of {group.Count} done");
        if (group.Count >= FewEntries)
            line.Append($" ({100 * done / group.Count}%)");

        foreach (var outcome in new[] { Outcome.Partial, Outcome.Skipped, Outcome.NoCheckIn })
        {
            var count = group.Count(c => c.Outcome == outcome);
            if (count > 0)
                line.Append($", {count} {Name(outcome)}");
        }

        if (group.Count < FewEntries)
            line.Append(" - too few to read into");
        return line.ToString();
    }

    public static string Name(Outcome outcome) => outcome switch
    {
        Outcome.Done => "done",
        Outcome.Partial => "partial",
        Outcome.Skipped => "skipped",
        Outcome.NoCheckIn => "no check-in",
        _ => "open",
    };
}
