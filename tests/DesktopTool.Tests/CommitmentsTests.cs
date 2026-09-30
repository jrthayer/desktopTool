using DesktopTool.Features.Commitments;
using Xunit;

namespace DesktopTool.Tests;

public sealed class CommitmentsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"commitments-test-{Guid.NewGuid():N}.json");
    private static readonly DateTime Noon = new(2026, 9, 29, 12, 0, 0);

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + ".tmp", _path + ".corrupt" })
            File.Delete(file);
    }

    [Theory]
    [InlineData("draft the intro 45m #Writing", "draft the intro", 45, "writing")]
    [InlineData("1.5h deep work", "deep work", 90, null)]
    [InlineData("taxes 1h30m", "taxes", 90, null)]
    [InlineData("call mum", "call mum", null, null)]
    [InlineData("fix the 3m timeout bug 20min", "fix the 3m timeout bug", 20, null)]
    [InlineData("read chapter 3", "read chapter 3", null, null)]
    public void Parser_pulls_estimate_and_tag(string input, string text, int? minutes, string? tag)
    {
        Assert.Equal((text, minutes, tag), CommitmentParser.Parse(input));
    }

    [Fact]
    public void Day_runs_four_am_to_four_am()
    {
        Assert.Equal(new DateOnly(2026, 9, 28), CommitmentLog.DayOf(new DateTime(2026, 9, 29, 1, 0, 0)));
        Assert.Equal(new DateOnly(2026, 9, 29), CommitmentLog.DayOf(new DateTime(2026, 9, 29, 4, 0, 0)));
    }

    [Fact]
    public void Add_is_capped_per_day_and_rejects_empty_text()
    {
        var log = new CommitmentLog(_path);
        Assert.Null(log.Add("45m #tag", Noon));
        for (var i = 0; i < CommitmentLog.DefaultMaxPerDay; i++)
            Assert.NotNull(log.Add($"thing {i}", Noon));
        Assert.Null(log.Add("one too many", Noon));
        Assert.NotNull(log.Add("tomorrow is fine", Noon.AddDays(1)));
    }

    [Fact]
    public void Status_follows_close_outs()
    {
        var log = new CommitmentLog(_path);
        var day = CommitmentLog.DayOf(Noon);
        Assert.Equal(DayStatus.Empty, log.StatusOf(day));

        var a = log.Add("a", Noon)!;
        var b = log.Add("b", Noon)!;
        Assert.Equal(DayStatus.InProgress, log.StatusOf(day));

        log.Close(a.Id, Outcome.Done, Noon);
        log.Close(b.Id, Outcome.Skipped, Noon, "  ran out of time ");
        Assert.Equal(DayStatus.CheckedIn, log.StatusOf(day));
        Assert.Equal("ran out of time", b.Note);

        log.Reopen(b.Id);
        log.Close(b.Id, Outcome.Done, Noon);
        Assert.Equal(DayStatus.Finished, log.StatusOf(day));
        Assert.Equal("ran out of time", b.Note);
    }

    [Fact]
    public void Sweep_closes_only_earlier_days_as_no_check_in()
    {
        var log = new CommitmentLog(_path);
        var old = log.Add("yesterday", Noon.AddDays(-1))!;
        var current = log.Add("today", Noon)!;

        Assert.Equal(1, log.SweepStale(Noon));
        Assert.Equal(Outcome.NoCheckIn, old.Outcome);
        Assert.Equal(new DateTime(2026, 9, 29, 4, 0, 0), old.ClosedAt);
        Assert.Equal(Outcome.Open, current.Outcome);
        Assert.Equal(0, log.SweepStale(Noon));
    }

    [Fact]
    public void Log_survives_a_restart()
    {
        var log = new CommitmentLog(_path);
        var added = log.Add("write it down 45m #Admin", Noon)!;
        log.Close(added.Id, Outcome.Partial, Noon.AddHours(1), "half of it");

        var reloaded = Assert.Single(new CommitmentLog(_path).All);
        Assert.Equal(added.Id, reloaded.Id);
        Assert.Equal(("write it down", 45, "admin"), (reloaded.Text, reloaded.EstimateMinutes, reloaded.Tag));
        Assert.Equal((Outcome.Partial, "half of it"), (reloaded.Outcome, reloaded.Note));
        Assert.Equal(new DateOnly(2026, 9, 29), reloaded.Day);
        Assert.Equal(Noon.AddHours(1), reloaded.ClosedAt);
    }

    [Fact]
    public void Corrupt_log_is_kept_aside_not_overwritten()
    {
        File.WriteAllText(_path, "{ not json");
        var log = new CommitmentLog(_path);
        Assert.Empty(log.All);
        log.Add("fresh start", Noon);
        Assert.Equal("{ not json", File.ReadAllText(_path + ".corrupt"));
    }

    [Fact]
    public void Review_counts_by_condition_and_flags_small_groups()
    {
        var log = new CommitmentLog(_path);
        for (var i = 0; i < 6; i++)
        {
            var morning = new DateTime(2026, 9, 20 + i, 9, 0, 0);
            log.Close(log.Add("small 20m #code", morning)!.Id, i < 5 ? Outcome.Done : Outcome.Partial, morning);
            var big = log.Add("big 3h", morning.AddHours(10))!;
            if (i < 2)
                log.Close(big.Id, Outcome.Skipped, morning.AddHours(11), "too tired");
        }
        log.SweepStale(Noon);

        var sections = Review.Build(log.All, Noon).ToDictionary(s => s.Title, s => s.Body);

        Assert.Contains("5 of 12 done (41%), 1 partial, 2 skipped, 4 no check-in", sections["Overview"]);
        Assert.Contains("Days where everything committed got done: 0 of 6", sections["Overview"]);
        Assert.Contains("30m or less: 5 of 6 done (83%), 1 partial", sections["By size"]);
        Assert.Contains("Over 2h: 0 of 6 done (0%), 2 skipped, 4 no check-in", sections["By size"]);
        Assert.Contains("Evening (5-10pm): 0 of 6 done", sections["By time committed"]);
        Assert.Contains("2 that day: 5 of 12 done", sections["By load"]);
        Assert.Contains("#code: 5 of 6 done", sections["By tag"]);
        Assert.Contains("too few to read into", sections["By weekday"]);
        Assert.Contains("big (skipped): too tired", sections["What got in the way"]);
    }

    [Fact]
    public void Review_of_an_empty_log_says_so()
    {
        var sections = Review.Build(new CommitmentLog(_path).All, Noon);
        Assert.Single(sections);
    }
}
