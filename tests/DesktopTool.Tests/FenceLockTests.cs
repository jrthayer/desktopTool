using System.Text.Json;
using DesktopTool.Features.FenceLocker;
using DesktopTool.Features.FenceLocker.Criteria;
using Xunit;

namespace DesktopTool.Tests;

public sealed class TimeWindowCriterionTests
{
    // 2026-11-02 is a Monday.
    private static DateTime Monday(int hour, int minute = 0) => new(2026, 11, 2, hour, minute, 0);

    [Fact]
    public void InsideAndOutsideADaytimeWindow()
    {
        var window = new TimeWindowCriterion { Start = new(18, 0), End = new(22, 0) };
        Assert.True(window.IsSatisfied(Monday(19)));
        Assert.False(window.IsSatisfied(Monday(17, 59)));
        Assert.False(window.IsSatisfied(Monday(23)));
    }

    [Fact]
    public void StartIsInclusiveEndIsExclusive()
    {
        var window = new TimeWindowCriterion { Start = new(18, 0), End = new(22, 0) };
        Assert.True(window.IsSatisfied(Monday(18)));
        Assert.False(window.IsSatisfied(Monday(22)));
    }

    [Fact]
    public void OvernightWindowCoversBothSidesOfMidnight()
    {
        var window = new TimeWindowCriterion { Start = new(22, 0), End = new(2, 0), Days = new() { DayOfWeek.Monday } };
        Assert.True(window.IsSatisfied(Monday(23)));
        // Tuesday 01:00 belongs to Monday's window.
        Assert.True(window.IsSatisfied(Monday(1).AddDays(1)));
        // Monday 01:00 belongs to Sunday's window, and Sunday isn't selected.
        Assert.False(window.IsSatisfied(Monday(1)));
        Assert.False(window.IsSatisfied(Monday(3).AddDays(1)));
    }

    [Fact]
    public void UnselectedDayIsClosed()
    {
        var window = new TimeWindowCriterion { Start = new(9, 0), End = new(17, 0), Days = new() { DayOfWeek.Saturday } };
        Assert.False(window.IsSatisfied(Monday(12)));
        Assert.True(window.IsSatisfied(Monday(12).AddDays(5)));
    }

    [Fact]
    public void NextOpeningFindsTheNextSelectedDay()
    {
        var window = new TimeWindowCriterion { Start = new(18, 0), End = new(22, 0), Days = new() { DayOfWeek.Wednesday } };
        Assert.Equal(new DateTime(2026, 11, 4, 18, 0, 0), window.NextOpening(Monday(12)));
    }
}

public sealed class PasswordCriterionTests
{
    [Fact]
    public void RightPasswordPassesWrongOneFails()
    {
        var criterion = PasswordCriterion.Create("hunter2");
        Assert.True(criterion.Verify("hunter2"));
        Assert.False(criterion.Verify("hunter3"));
        Assert.False(criterion.Verify(""));
        Assert.False(criterion.Verify(null));
    }

    [Fact]
    public void HashIsSaltedAndNeverThePassword()
    {
        var a = PasswordCriterion.Create("same");
        var b = PasswordCriterion.Create("same");
        Assert.DoesNotContain("same", a.Hash);
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);
    }
}

public sealed class FenceLockServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-locks-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 2, 12, 0, 0);

    public FenceLockServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private FenceLockService NewService() =>
        new(new FenceLockStore(Path.Combine(_dir, "locks.json")), new UnlockLog(Path.Combine(_dir, "log.json")), () => _now);

    [Fact]
    public void AnyOneCriterionOpensIt()
    {
        var service = NewService();
        var id = Guid.NewGuid();
        service.SetLock(new FenceLock
        {
            FenceId = id,
            Criteria = { PasswordCriterion.Create("pw"), new TimeWindowCriterion { Start = new(11, 0), End = new(13, 0) } },
        });

        Assert.False(service.IsLocked(id));
        Assert.Equal(FenceLockStatus.OpenByTime, service.GetStatus(id));

        _now = _now.AddHours(3);
        Assert.True(service.IsLocked(id));
    }

    [Fact]
    public void PromptUnlockLastsTheSessionAndIsLogged()
    {
        var service = NewService();
        var id = Guid.NewGuid();
        service.SetLock(new FenceLock { FenceId = id, Criteria = { new SpecialEventCriterion() } });

        var prompts = 0;
        service.UnlockPrompt = (_, _, _) => { prompts++; return new UnlockGrant("Special event", "Deadline tonight"); };

        Assert.True(service.EnsureUnlocked(id, "Games", null));
        Assert.True(service.EnsureUnlocked(id, "Games", null));
        Assert.Equal(1, prompts);
        Assert.Equal(FenceLockStatus.UnlockedThisSession, service.GetStatus(id));

        var entry = Assert.Single(service.Log.Entries);
        Assert.Equal("Special event", entry.Type);
        Assert.Equal("Deadline tonight", entry.Reason);
        Assert.Equal("Games", entry.FenceName);

        service.LockAgain(id);
        Assert.True(service.IsLocked(id));
    }

    [Fact]
    public void CancelledPromptKeepsItLocked()
    {
        var service = NewService();
        var id = Guid.NewGuid();
        service.SetLock(new FenceLock { FenceId = id, Criteria = { PasswordCriterion.Create("pw") } });
        service.UnlockPrompt = (_, _, _) => null;

        Assert.False(service.EnsureUnlocked(id, "Games", null));
        Assert.True(service.IsLocked(id));
        Assert.Empty(service.Log.Entries);
    }

    [Fact]
    public void TimeWindowUseIsLoggedOncePerWindow()
    {
        var service = NewService();
        var id = Guid.NewGuid();
        service.SetLock(new FenceLock { FenceId = id, Criteria = { new TimeWindowCriterion { Start = new(11, 0), End = new(13, 0) } } });

        Assert.True(service.EnsureUnlocked(id, "Games", null));
        _now = _now.AddMinutes(20);
        Assert.True(service.EnsureUnlocked(id, "Games", null));
        Assert.Single(service.Log.Entries);

        // The next day's window is a new window.
        _now = _now.AddDays(1);
        Assert.True(service.EnsureUnlocked(id, "Games", null));
        Assert.Equal(2, service.Log.Entries.Count);
        Assert.All(service.Log.Entries, e => Assert.Equal("Time window", e.Type));
    }

    [Fact]
    public void NoLockMeansNoPrompt()
    {
        var service = NewService();
        service.UnlockPrompt = (_, _, _) => throw new InvalidOperationException("should not prompt");
        Assert.True(service.EnsureUnlocked(Guid.NewGuid(), "Anything", null));
    }

    [Fact]
    public void ApplyingALoadoutCopiesItAndLocksStraightAway()
    {
        var service = NewService();
        var loadout = new LockLoadout { Name = "Focus", Reason = "Remember why", Criteria = { PasswordCriterion.Create("pw") } };
        var id = Guid.NewGuid();

        Assert.True(service.ApplyLoadout(loadout, id, "Games", null));
        var applied = service.GetLock(id)!;
        Assert.Equal("Remember why", applied.Reason);
        Assert.NotSame(loadout.Criteria[0], applied.Criteria[0]);
        Assert.True(((PasswordCriterion)applied.Criteria[0]).Verify("pw"));
        Assert.True(service.IsLocked(id));

        // Replacing a lock is changing it - a cancelled prompt refuses.
        service.UnlockPrompt = (_, _, _) => null;
        Assert.False(service.ApplyLoadout(loadout, id, "Games", null));
    }

    [Fact]
    public void LoadoutLibrarySavesUpdatesAndRemoves()
    {
        var path = Path.Combine(_dir, "loadouts.json");
        var library = new LockLoadoutLibrary(path);
        var loadout = new LockLoadout { Name = "A", Criteria = { new SpecialEventCriterion() } };
        library.Save(loadout);
        library.Save(new LockLoadout { Id = loadout.Id, Name = "B", Criteria = { new SpecialEventCriterion() } });

        var reloaded = new LockLoadoutLibrary(path);
        Assert.Equal("B", Assert.Single(reloaded.Loadouts).Name);

        reloaded.Remove(loadout.Id);
        Assert.Empty(new LockLoadoutLibrary(path).Loadouts);
    }

    [Fact]
    public void LocksAndLogSurviveAReload()
    {
        var id = Guid.NewGuid();
        var first = NewService();
        first.SetLock(new FenceLock
        {
            FenceId = id,
            Reason = "Focus",
            Criteria = { PasswordCriterion.Create("pw"), new SpecialEventCriterion(), new TimeWindowCriterion { Start = new(20, 0), End = new(21, 0), Days = new() { DayOfWeek.Friday } } },
        });
        first.Grant(id, "Games", new UnlockGrant("Password", null));

        var second = NewService();
        var reloaded = second.GetLock(id)!;
        Assert.Equal("Focus", reloaded.Reason);
        Assert.IsType<PasswordCriterion>(reloaded.Criteria[0]);
        Assert.True(((PasswordCriterion)reloaded.Criteria[0]).Verify("pw"));
        Assert.IsType<SpecialEventCriterion>(reloaded.Criteria[1]);
        var window = Assert.IsType<TimeWindowCriterion>(reloaded.Criteria[2]);
        Assert.Equal(new TimeOnly(20, 0), window.Start);
        Assert.Equal(new[] { DayOfWeek.Friday }, window.Days);
        // The session unlock isn't saved; the log entry is.
        Assert.True(second.IsLocked(id));
        Assert.Single(second.Log.Entries);
    }
}
