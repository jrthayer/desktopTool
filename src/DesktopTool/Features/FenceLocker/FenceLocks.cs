using DesktopTool.Features.FenceLocker.UI;

namespace DesktopTool.Features.FenceLocker;

/// <summary>The app-wide FenceLockService, plus the one-minute timer that notices time windows
/// opening and closing. Static so both fence types (and the Fence Locker widget) can reach it
/// without being handed a reference, the same way DesktopHider works.</summary>
internal static class FenceLocks
{
    private static FenceLockService? _service;
    private static System.Windows.Forms.Timer? _timer;
    private static Dictionary<Guid, FenceLockStatus> _lastStatuses = new();

    public static FenceLockService Service =>
        _service ?? throw new InvalidOperationException("FenceLocks.Initialize hasn't been called.");

    /// <summary>The saved lock loadouts the Fence Locker widget lists and places.</summary>
    public static LockLoadoutLibrary Loadouts { get; private set; } = null!;

    /// <summary>Raised on any lock change, unlock, or time window opening/closing.</summary>
    public static event EventHandler? Changed;

    public static void Initialize()
    {
        Loadouts = new LockLoadoutLibrary();
        _service = new FenceLockService(new FenceLockStore(), new UnlockLog())
        {
            UnlockPrompt = (fenceLock, name, owner) => UnlockDialog.Prompt(fenceLock, name, owner),
        };
        _service.Changed += (_, _) =>
        {
            _lastStatuses = _service.SnapshotStatuses();
            Changed?.Invoke(null, EventArgs.Empty);
        };
        _lastStatuses = _service.SnapshotStatuses();

        _timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _timer.Tick += (_, _) =>
        {
            var statuses = _service.SnapshotStatuses();
            var changed = statuses.Count != _lastStatuses.Count
                || statuses.Any(s => !_lastStatuses.TryGetValue(s.Key, out var old) || old != s.Value);
            _lastStatuses = statuses;
            if (changed)
                Changed?.Invoke(null, EventArgs.Empty);
        };
        _timer.Start();
    }

    public static void Shutdown()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    public static FenceLockStatus GetStatus(Guid fenceId) => _service?.GetStatus(fenceId) ?? FenceLockStatus.None;

    /// <summary>See FenceLockService.EnsureUnlocked - true if there's no lock service yet.</summary>
    public static bool EnsureUnlocked(Guid fenceId, string fenceName, IWin32Window? owner) =>
        _service?.EnsureUnlocked(fenceId, fenceName, owner) ?? true;

    /// <summary>Called when a fence is deleted, so its lock doesn't linger.</summary>
    public static void RemoveLock(Guid fenceId) => _service?.RemoveLock(fenceId);
}
