using DesktopTool.Features.FenceLocker.Criteria;

namespace DesktopTool.Features.FenceLocker;

public enum FenceLockStatus
{
    /// <summary>No lock on this fence.</summary>
    None,
    /// <summary>Locked - nothing currently lets it open.</summary>
    Locked,
    /// <summary>Has a lock, but a time window is open right now.</summary>
    OpenByTime,
    /// <summary>Has a lock, but was unlocked (password, special event) earlier this session.</summary>
    UnlockedThisSession,
}

/// <summary>What a successful unlock prompt hands back - which criterion type let you in, and the
/// reason you typed (special events only).</summary>
public sealed record UnlockGrant(string Type, string? Reason);

/// <summary>Decides whether a fence is locked, runs the unlock prompt, keeps the session's unlocks,
/// and records every unlock in the history. Pure logic: the prompt itself is supplied as a delegate
/// (see UnlockPrompt), so this class has no UI of its own and can be tested directly. The app uses
/// one instance, through FenceLocks.</summary>
public sealed class FenceLockService
{
    private readonly FenceLockStore _store;
    private readonly List<FenceLock> _locks;
    private readonly HashSet<Guid> _unlockedThisSession = new();
    // Each fence's time window already logged as used, so a window is logged once, not per launch.
    private readonly Dictionary<Guid, DateTime> _loggedWindows = new();
    private readonly Func<DateTime> _clock;

    public FenceLockService(FenceLockStore store, UnlockLog log, Func<DateTime>? clock = null)
    {
        _store = store;
        Log = log;
        _locks = store.Load();
        _clock = clock ?? (() => DateTime.Now);
    }

    public UnlockLog Log { get; }

    /// <summary>Shows the unlock prompt for a locked fence and returns what let you in, or null if you
    /// cancelled. Set by the UI (FenceLocks.Initialize).</summary>
    public Func<FenceLock, string, IWin32Window?, UnlockGrant?>? UnlockPrompt { get; set; }

    /// <summary>Raised when a lock is added, changed or removed, or a fence is unlocked or locked again.</summary>
    public event EventHandler? Changed;

    public DateTime Now => _clock();

    public FenceLock? GetLock(Guid fenceId) => _locks.FirstOrDefault(l => l.FenceId == fenceId);

    public void SetLock(FenceLock fenceLock)
    {
        _locks.RemoveAll(l => l.FenceId == fenceLock.FenceId);
        _locks.Add(fenceLock);
        _store.Save(_locks);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveLock(Guid fenceId)
    {
        if (_locks.RemoveAll(l => l.FenceId == fenceId) == 0)
            return;
        _unlockedThisSession.Remove(fenceId);
        _loggedWindows.Remove(fenceId);
        _store.Save(_locks);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public FenceLockStatus GetStatus(Guid fenceId)
    {
        if (GetLock(fenceId) is not { } fenceLock)
            return FenceLockStatus.None;
        if (_unlockedThisSession.Contains(fenceId))
            return FenceLockStatus.UnlockedThisSession;
        return fenceLock.Criteria.Any(c => c.IsSatisfied(Now)) ? FenceLockStatus.OpenByTime : FenceLockStatus.Locked;
    }

    public bool IsLocked(Guid fenceId) => GetStatus(fenceId) == FenceLockStatus.Locked;

    /// <summary>The gate every locked action goes through. True straight away for a fence with no
    /// lock, one unlocked earlier this session, or one inside an open time window (logged the first
    /// time it's used in that window). Otherwise shows the unlock prompt; a successful unlock lasts
    /// until Desktop Tool restarts and is logged.</summary>
    public bool EnsureUnlocked(Guid fenceId, string fenceName, IWin32Window? owner)
    {
        if (GetLock(fenceId) is not { } fenceLock || _unlockedThisSession.Contains(fenceId))
            return true;

        var now = Now;
        foreach (var window in fenceLock.Criteria.OfType<TimeWindowCriterion>())
        {
            if (window.CurrentWindowStart(now) is not { } windowStart)
                continue;
            if (!_loggedWindows.TryGetValue(fenceId, out var logged) || logged != windowStart)
            {
                _loggedWindows[fenceId] = windowStart;
                Log.Append(new UnlockLogEntry(now.ToUniversalTime(), fenceId, fenceName, window.UnlockType, null));
            }
            return true;
        }

        // Any other passive criterion a future type might add.
        if (fenceLock.Criteria.FirstOrDefault(c => c is not TimeWindowCriterion && c.IsSatisfied(now)) is { } passive)
        {
            Log.Append(new UnlockLogEntry(now.ToUniversalTime(), fenceId, fenceName, passive.UnlockType, null));
            return true;
        }

        if (UnlockPrompt?.Invoke(fenceLock, fenceName, owner) is not { } grant)
            return false;

        Grant(fenceId, fenceName, grant);
        return true;
    }

    /// <summary>Records a successful unlock - also what EnsureUnlocked does after the prompt.</summary>
    public void Grant(Guid fenceId, string fenceName, UnlockGrant grant)
    {
        _unlockedThisSession.Add(fenceId);
        Log.Append(new UnlockLogEntry(Now.ToUniversalTime(), fenceId, fenceName, grant.Type, grant.Reason));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Puts a copy of loadout on a fence, replacing any lock it had, and locks it straight
    /// away (a session unlock from the old lock doesn't carry over to the new one). Returns false
    /// without changing anything if the fence's current lock wasn't met - replacing a lock is
    /// changing it.</summary>
    public bool ApplyLoadout(LockLoadout loadout, Guid fenceId, string fenceName, IWin32Window? owner)
    {
        if (!EnsureUnlocked(fenceId, fenceName, owner))
            return false;
        _unlockedThisSession.Remove(fenceId);
        _loggedWindows.Remove(fenceId);
        SetLock(new FenceLock { FenceId = fenceId, Reason = loadout.Reason, Criteria = FenceLockerJson.Clone(loadout.Criteria) });
        return true;
    }

    /// <summary>Ends a session unlock early ("Lock").</summary>
    public void LockAgain(Guid fenceId)
    {
        if (_unlockedThisSession.Remove(fenceId))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every locked fence's status right now - the timer compares these to notice a time
    /// window opening or closing.</summary>
    public Dictionary<Guid, FenceLockStatus> SnapshotStatuses() =>
        _locks.ToDictionary(l => l.FenceId, l => GetStatus(l.FenceId));

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
