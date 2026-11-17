using System.Text.Json;

namespace DesktopTool.Features.WindowSnapping;

/// <summary>Same shape as WidgetManagerStore (plain JSON file under %AppData%\DesktopTool,
/// corrupt-or-missing-file-starts-fresh), for the single WindowSnappingSettings.</summary>
public sealed class WindowSnappingStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "window-snapping.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public WindowSnappingSettings Load()
    {
        if (!File.Exists(FilePath))
            return new WindowSnappingSettings();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<WindowSnappingSettings>(json, SerializerOptions) ?? new WindowSnappingSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new WindowSnappingSettings();
        }
    }

    public void Save(WindowSnappingSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
