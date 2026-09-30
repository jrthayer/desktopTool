using System.Text.Json;

namespace DesktopTool.Features.Commitments;

/// <summary>Same shape as ClaudePipelineWidgetStore (plain JSON file under %AppData%\DesktopTool, for
/// a single model rather than a list - there's only ever one Commitments widget).</summary>
public sealed class CommitmentsWidgetStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "commitments-widget.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public CommitmentsWidgetModel Load()
    {
        if (!File.Exists(FilePath))
            return new CommitmentsWidgetModel();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<CommitmentsWidgetModel>(json, SerializerOptions) ?? new CommitmentsWidgetModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new CommitmentsWidgetModel();
        }
    }

    public void Save(CommitmentsWidgetModel model)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(model, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
