using System.Text.Json;

namespace DesktopTool.Features.Fences;

public sealed class SnapLineSettings
{
    public List<SnapLineModel> Lines { get; set; } = new();

    /// <summary>Screen.DeviceName of every monitor that's already had its default edge snap lines
    /// seeded (see SnapLineManager) - lets a first-ever launch (or a newly-connected monitor later)
    /// get default Top/Bottom/Left/Right lines automatically, without ever re-adding them once the
    /// user has deleted some or all of them for a monitor that's already been seeded once.</summary>
    public HashSet<string> SeededMonitors { get; set; } = new();

    /// <summary>Off drops every custom snap line (seeded defaults included - they're plain entries
    /// in Lines, not distinguished from a user-drawn one) from drag candidates app-wide, while
    /// widget-to-widget edge snapping keeps working untouched - see SnapLineManager.Enabled/SetEnabled.
    /// Editing lines via Manage Snap Lines... still works regardless of this.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Off drops every other live widget's edges (see LayeredWidgetForm.GetOtherWidgetEdges)
    /// from drag/resize candidates app-wide, while custom snap lines above keep working untouched -
    /// see SnapLineManager.WidgetEdgesEnabled/SetWidgetEdgesEnabled.</summary>
    public bool WidgetEdgesEnabled { get; set; } = true;
}

public sealed class SnapLineStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "snaplines.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public SnapLineSettings Load()
    {
        if (!File.Exists(FilePath))
            return new SnapLineSettings();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<SnapLineSettings>(json, SerializerOptions) ?? new SnapLineSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable config: start fresh rather than crash the app.
            return new SnapLineSettings();
        }
    }

    public void Save(SnapLineSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
