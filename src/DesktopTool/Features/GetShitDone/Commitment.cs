namespace DesktopTool.Features.GetShitDone;

/// <summary>How a commitment ended. NoCheckIn is never chosen by hand - it's what an Open commitment
/// becomes once its day has passed without being closed out (see CommitmentLog.SweepStale), so a
/// day that was simply abandoned still shows up in the review instead of vanishing.</summary>
public enum Outcome
{
    Open,
    Done,
    Partial,
    Skipped,
    NoCheckIn,
}

/// <summary>One thing promised for one day, plus the conditions it was promised under - the review
/// (see Review) compares outcomes across those conditions. Times are local wall-clock on purpose:
/// "what hour of the day was it" is the question being asked of them.</summary>
public sealed class Commitment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;

    /// <summary>From a "#tag" in the typed text, lower-cased - null when none was given.</summary>
    public string? Tag { get; set; }

    /// <summary>From a duration in the typed text ("45m", "1.5h", "1h30m") - null when none was given.</summary>
    public int? EstimateMinutes { get; set; }

    /// <summary>The day this belongs to - see CommitmentLog.DayOf, which isn't simply the calendar date.</summary>
    public DateOnly Day { get; set; }

    public DateTime CreatedAt { get; set; }
    public Outcome Outcome { get; set; } = Outcome.Open;
    public DateTime? ClosedAt { get; set; }

    /// <summary>Optional "what got in the way" (or anything else worth remembering) added at close-out.</summary>
    public string? Note { get; set; }
}
