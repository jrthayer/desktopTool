using System.Text.Json;

namespace DesktopTool.Features.ClaudePipeline;

/// <summary>Same shape as LayoutStore (plain JSON file under %AppData%\DesktopTool, corrupt-or-
/// missing-file-starts-fresh) - persists the list of user-defined PipelineFeatures.</summary>
public sealed class ClaudePipelineFeatureStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "claude-pipeline.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public List<PipelineFeature> Load()
    {
        if (!File.Exists(FilePath))
            return new List<PipelineFeature>();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<PipelineFeature>>(json, SerializerOptions) ?? new List<PipelineFeature>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable config: start fresh rather than crash the app.
            return new List<PipelineFeature>();
        }
    }

    public void Save(IReadOnlyList<PipelineFeature> features)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(features, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
