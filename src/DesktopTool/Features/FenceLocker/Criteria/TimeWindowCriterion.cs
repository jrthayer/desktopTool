using System.Text.Json.Serialization;
namespace DesktopTool.Features.FenceLocker.Criteria;

/// <summary>Open during set hours on set days. Start is inclusive, End exclusive. A window whose End
/// is earlier than its Start runs overnight (22:00–02:00): it belongs to the day it starts on, so
/// the early-morning part counts if the previous day is selected. Start == End means all day.</summary>
public sealed class TimeWindowCriterion : LockCriterion
{
    private static readonly DayOfWeek[] AllDays =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

    public TimeOnly Start { get; set; } = new(18, 0);
    public TimeOnly End { get; set; } = new(22, 0);
    public List<DayOfWeek> Days { get; set; } = AllDays.ToList();

    [JsonIgnore]
    public override string UnlockType => "Time window";

    public override string Describe() => $"{DescribeDays()} {Start:HH\\:mm}–{End:HH\\:mm}";

    public override bool IsSatisfied(DateTime now) => CurrentWindowStart(now) is not null;

    /// <summary>When the window that's open right now began - null if none is open. Used to log a
    /// time-window unlock once per window rather than on every launch.</summary>
    public DateTime? CurrentWindowStart(DateTime now)
    {
        var time = TimeOnly.FromDateTime(now);
        var today = now.Date;

        if (Start == End)
            return Days.Contains(now.DayOfWeek) ? today : null;

        if (Start < End)
            return Days.Contains(now.DayOfWeek) && time >= Start && time < End ? today + Start.ToTimeSpan() : null;

        // Overnight window.
        if (time >= Start && Days.Contains(now.DayOfWeek))
            return today + Start.ToTimeSpan();
        var yesterday = today.AddDays(-1);
        if (time < End && Days.Contains(yesterday.DayOfWeek))
            return yesterday + Start.ToTimeSpan();
        return null;
    }

    /// <summary>When the window next opens after now - null if no days are selected.</summary>
    public DateTime? NextOpening(DateTime now)
    {
        for (var offset = 0; offset <= 7; offset++)
        {
            var day = now.Date.AddDays(offset);
            if (!Days.Contains(day.DayOfWeek))
                continue;
            var opening = day + Start.ToTimeSpan();
            if (opening > now)
                return opening;
        }
        return null;
    }

    private string DescribeDays()
    {
        var set = Days.ToHashSet();
        if (set.Count == 7)
            return "Every day";
        if (set.SetEquals(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }))
            return "Weekdays";
        if (set.SetEquals(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }))
            return "Weekends";
        return string.Join(", ", AllDays.Where(set.Contains).Select(d => d.ToString()[..3]));
    }
}
