using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopTool.Features.FenceLocker.Criteria;

namespace DesktopTool.Features.FenceLocker;

/// <summary>A lock on one fence or folder fence (by its Id). Reason is the note you wrote when
/// locking it, shown in every unlock prompt as a reminder. Any one criterion opens it.</summary>
public sealed class FenceLock
{
    public Guid FenceId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<LockCriterion> Criteria { get; set; } = new();
}

/// <summary>A saved lock setup - name, reminder and ways in - ready to be placed on any number of
/// fences (see FenceLockService.ApplyLoadout). Placing it copies it; editing the loadout later
/// doesn't change fences it was already placed on.</summary>
public sealed class LockLoadout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Lock";
    public string Reason { get; set; } = string.Empty;
    public List<LockCriterion> Criteria { get; set; } = new();

    /// <summary>A one-line summary of its ways in, e.g. "Password · Weekdays 18:00–22:00".</summary>
    public string Describe() => Criteria.Count == 0 ? "No ways in" : string.Join(" · ", Criteria.Select(c => c.Describe()));
}

/// <summary>Every saved lock loadout, in %AppData%\DesktopTool\fence-lock-loadouts.json. The path can
/// be overridden for tests.</summary>
public sealed class LockLoadoutLibrary
{
    private readonly string _path;
    private readonly List<LockLoadout> _loadouts;

    public LockLoadoutLibrary(string? path = null)
    {
        _path = path ?? Path.Combine(FenceLockerJson.DirectoryPath, "fence-lock-loadouts.json");
        _loadouts = FenceLockerJson.LoadList<LockLoadout>(_path);
    }

    public IReadOnlyList<LockLoadout> Loadouts => _loadouts;

    public event EventHandler? Changed;

    /// <summary>Adds loadout, or replaces the one with the same Id.</summary>
    public void Save(LockLoadout loadout)
    {
        var index = _loadouts.FindIndex(l => l.Id == loadout.Id);
        if (index >= 0)
            _loadouts[index] = loadout;
        else
            _loadouts.Add(loadout);
        Persist();
    }

    public void Remove(Guid id)
    {
        if (_loadouts.RemoveAll(l => l.Id == id) > 0)
            Persist();
    }

    private void Persist()
    {
        FenceLockerJson.SaveList(_path, _loadouts);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One unlock, for the history. Reason is only set for a special event.</summary>
public sealed record UnlockLogEntry(DateTime Utc, Guid FenceId, string FenceName, string Type, string? Reason);

internal static class FenceLockerJson
{
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static List<T> LoadList<T>(string path)
    {
        if (!File.Exists(path))
            return new List<T>();
        try
        {
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Options) ?? new List<T>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new List<T>();
        }
    }

    internal static void SaveList<T>(string path, List<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(items, Options));
    }

    /// <summary>A deep copy, through the same JSON the stores use - so placing a loadout on a fence
    /// never shares criterion objects between the two.</summary>
    internal static List<LockCriterion> Clone(List<LockCriterion> criteria) =>
        JsonSerializer.Deserialize<List<LockCriterion>>(JsonSerializer.Serialize(criteria, Options), Options) ?? new();
}

/// <summary>Every fence's lock, saved to %AppData%\DesktopTool\fence-locks.json. The path can be
/// overridden for tests.</summary>
public sealed class FenceLockStore
{
    private readonly string _path;

    public FenceLockStore(string? path = null) =>
        _path = path ?? Path.Combine(FenceLockerJson.DirectoryPath, "fence-locks.json");

    public List<FenceLock> Load() => FenceLockerJson.LoadList<FenceLock>(_path);

    public void Save(List<FenceLock> locks) => FenceLockerJson.SaveList(_path, locks);
}

/// <summary>The unlock history, saved to %AppData%\DesktopTool\fence-unlock-log.json - append only.
/// The path can be overridden for tests.</summary>
public sealed class UnlockLog
{
    private readonly string _path;
    private readonly List<UnlockLogEntry> _entries;

    public UnlockLog(string? path = null)
    {
        _path = path ?? Path.Combine(FenceLockerJson.DirectoryPath, "fence-unlock-log.json");
        _entries = FenceLockerJson.LoadList<UnlockLogEntry>(_path);
    }

    /// <summary>Oldest first, as they were recorded.</summary>
    public IReadOnlyList<UnlockLogEntry> Entries => _entries;

    public event EventHandler? Changed;

    public void Append(UnlockLogEntry entry)
    {
        _entries.Add(entry);
        FenceLockerJson.SaveList(_path, _entries);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
