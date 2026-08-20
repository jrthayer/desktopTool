using System.Text.Json;

namespace DesktopTool.Features.ClaudePipeline;

/// <summary>Same shape as LayoutLauncherStore (plain JSON file under %AppData%\DesktopTool, for a
/// single model rather than a list - there's only ever one Claude Pipeline widget).</summary>
public sealed class ClaudePipelineWidgetStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "claude-pipeline-widget.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public ClaudePipelineModel Load()
    {
        if (!File.Exists(FilePath))
            return new ClaudePipelineModel();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<ClaudePipelineModel>(json, SerializerOptions) ?? new ClaudePipelineModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ClaudePipelineModel();
        }
    }

    public void Save(ClaudePipelineModel model)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(model, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
