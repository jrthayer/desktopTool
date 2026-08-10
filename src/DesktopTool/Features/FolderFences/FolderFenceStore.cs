using System.Text.Json;

namespace DesktopTool.Features.FolderFences;

public sealed class FolderFenceStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "folderfences.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public List<FolderFenceModel> Load()
    {
        if (!File.Exists(FilePath))
            return new List<FolderFenceModel>();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<FolderFenceModel>>(json, SerializerOptions) ?? new List<FolderFenceModel>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable config: start fresh rather than crash the app.
            return new List<FolderFenceModel>();
        }
    }

    public void Save(IReadOnlyList<FolderFenceModel> models)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(models, SerializerOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }
}
