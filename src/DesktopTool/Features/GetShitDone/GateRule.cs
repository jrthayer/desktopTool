namespace DesktopTool.Features.GetShitDone;

/// <summary>
/// What has to be true of today before a gate (see ProgramGate/ShutdownGate) lets something through -
/// every requirement that's set has to hold. Pure: it only reads the log, so it's the one part of
/// gating the tests cover. Not referenced by the widget yet - nothing constructs a gate.
/// </summary>
public sealed class GateRule
{
    /// <summary>How many of today's commitments have to be closed out. 0 asks for none.</summary>
    public int MinClosed { get; init; }

    /// <summary>Count only Done toward MinClosed. Off by default: outcomes are self-reported, so a
    /// gate that only opens for Done pays for ticking things off that weren't, and the review is
    /// only worth reading while the log is honest.</summary>
    public bool DoneOnly { get; init; }

    /// <summary>Nothing of today's may still be Open - the "check in before you leave" rule.</summary>
    public bool NothingOpen { get; init; }

    /// <summary>Wall-clock time the gate stays shut until, null for no time requirement. Measured
    /// within CommitmentLog's own 4am-to-4am day, so 1am still counts as after 5pm.</summary>
    public TimeOnly? NotBefore { get; init; }

    /// <summary>Null when every requirement holds, otherwise what's still missing, worded to be
    /// shown as-is.</summary>
    public string? Unmet(CommitmentLog log, DateTime now)
    {
        var today = log.ForDay(CommitmentLog.DayOf(now));

        var counted = today.Count(c => DoneOnly ? c.Outcome == Outcome.Done : c.Outcome != Outcome.Open);
        if (counted < MinClosed)
            return $"{counted} of {MinClosed} {(DoneOnly ? "done" : "closed out")} today.";

        var open = today.Count(c => c.Outcome == Outcome.Open);
        if (NothingOpen && open > 0)
            return $"{open} still open today.";

        if (NotBefore is { } notBefore && IntoDay(TimeOnly.FromDateTime(now)) < IntoDay(notBefore))
            return $"Not before {notBefore:t}.";

        return null;
    }

    private static TimeSpan IntoDay(TimeOnly time) =>
        time.ToTimeSpan() + (time.Hour < CommitmentLog.DayStartHour ? TimeSpan.FromDays(1) : TimeSpan.Zero);
}
