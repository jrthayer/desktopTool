using System.Text.Json;

namespace DesktopTool.Features.GetShitDone;

/// <summary>Same shape as ClaudePipelineWidgetStore (plain JSON file under %AppData%\DesktopTool, for
/// a single model rather than a list - there's only ever one Get Shit Done widget).</summary>
public sealed class GetShitDoneWidgetStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "commitments-widget.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public GetShitDoneWidgetModel Load()
    {
        if (!File.Exists(FilePath))
            return new GetShitDoneWidgetModel();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<GetShitDoneWidgetModel>(json, SerializerOptions) ?? new GetShitDoneWidgetModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new GetShitDoneWidgetModel();
        }
    }

    public void Save(GetShitDoneWidgetModel model)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(model, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
