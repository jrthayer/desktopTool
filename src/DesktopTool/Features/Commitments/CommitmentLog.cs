using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopTool.Features.Commitments;

public enum DayStatus
{
    /// <summary>Nothing committed for the day.</summary>
    Empty,
    /// <summary>At least one commitment still Open.</summary>
    InProgress,
    /// <summary>Everything committed has been closed out, not all of it Done.</summary>
    CheckedIn,
    /// <summary>Everything committed is Done - the day's finish line.</summary>
    Finished,
}

/// <summary>
/// The whole log - every commitment ever made, in one JSON file under %AppData%\DesktopTool like
/// every other store here. Unlike those, a corrupt file isn't simply started fresh over (see Load) -
/// this is the one file in the app that's a record rather than a setting.
/// </summary>
public sealed class CommitmentLog
{
    public const int DefaultMaxPerDay = 3;

    /// <summary>A "day" runs 4am to 4am, so something closed out at 1am still counts toward the day
    /// it was promised for rather than being swept as abandoned at midnight.</summary>
    public const int DayStartHour = 4;

    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool", "commitments.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private List<Commitment> _items = new();

    public event EventHandler? Changed;

    /// <summary>path is only ever passed by tests - the app itself always uses DefaultPath.</summary>
    public CommitmentLog(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    public static DateOnly DayOf(DateTime now) => DateOnly.FromDateTime(now.AddHours(-DayStartHour));

    public IReadOnlyList<Commitment> All => _items;

    public IReadOnlyList<Commitment> ForDay(DateOnly day) =>
        _items.Where(c => c.Day == day).OrderBy(c => c.CreatedAt).ToList();

    public DayStatus StatusOf(DateOnly day)
    {
        var items = ForDay(day);
        if (items.Count == 0)
            return DayStatus.Empty;
        if (items.Any(c => c.Outcome == Outcome.Open))
            return DayStatus.InProgress;
        return items.All(c => c.Outcome == Outcome.Done) ? DayStatus.Finished : DayStatus.CheckedIn;
    }

    /// <summary>Null when the text is empty once its estimate/tag are taken out, or when the day
    /// already has maxPerDay commitments - the cap is what keeps the day's finish line reachable.</summary>
    public Commitment? Add(string input, DateTime now, int maxPerDay = DefaultMaxPerDay)
    {
        var (text, estimate, tag) = CommitmentParser.Parse(input);
        var day = DayOf(now);
        if (text.Length == 0 || _items.Count(c => c.Day == day) >= maxPerDay)
            return null;

        var commitment = new Commitment
        {
            Text = text,
            EstimateMinutes = estimate,
            Tag = tag,
            Day = day,
            CreatedAt = now,
        };
        _items.Add(commitment);
        Save();
        return commitment;
    }

    public bool Close(Guid id, Outcome outcome, DateTime now, string? note = null)
    {
        if (outcome == Outcome.Open || Find(id) is not { } commitment)
            return false;

        commitment.Outcome = outcome;
        commitment.ClosedAt = now;
        if (!string.IsNullOrWhiteSpace(note))
            commitment.Note = note.Trim();
        Save();
        return true;
    }

    /// <summary>Undoes a close-out (a misclick, or a NoCheckIn that was really done). Keeps the note.</summary>
    public bool Reopen(Guid id)
    {
        if (Find(id) is not { } commitment || commitment.Outcome == Outcome.Open)
            return false;

        commitment.Outcome = Outcome.Open;
        commitment.ClosedAt = null;
        Save();
        return true;
    }

    public bool SetNote(Guid id, string? note)
    {
        if (Find(id) is not { } commitment)
            return false;

        commitment.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Save();
        return true;
    }

    /// <summary>Closes every commitment still Open from a day before today as NoCheckIn. Returns how
    /// many it closed.</summary>
    public int SweepStale(DateTime now)
    {
        var today = DayOf(now);
        var stale = _items.Where(c => c.Outcome == Outcome.Open && c.Day < today).ToList();
        if (stale.Count == 0)
            return 0;

        foreach (var commitment in stale)
        {
            commitment.Outcome = Outcome.NoCheckIn;
            // The end of its own day, not "now" - when the sweep happened to run says nothing about it.
            commitment.ClosedAt = commitment.Day.AddDays(1).ToDateTime(new TimeOnly(DayStartHour, 0));
        }
        Save();
        return stale.Count;
    }

    private Commitment? Find(Guid id) => _items.FirstOrDefault(c => c.Id == id);

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        try
        {
            _items = JsonSerializer.Deserialize<List<Commitment>>(File.ReadAllText(_path), SerializerOptions) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Kept aside rather than left to be overwritten by the next Save - unlike a widget's
            // position, this can't be recreated by just setting it again.
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_items, SerializerOptions));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
