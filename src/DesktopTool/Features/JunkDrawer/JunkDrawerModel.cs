using System.Text.Json;
using DesktopTool.UI;

namespace DesktopTool.Features.JunkDrawer;

/// <summary>Persisted state for the Junk Drawer widget - position, size, title, visibility, plus the
/// shared IWidgetStyle knobs from WidgetStyleModel. The tools themselves keep their own state
/// (registry settings, fence visibility), so nothing about them is stored here.</summary>
public sealed class JunkDrawerModel : WidgetStyleModel
{
    /// <summary>Null until the widget has been moved/resized once - see JunkDrawerWidget's own
    /// CreateParams, which centers it on the primary screen until then.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 260;
    public int? Height { get; set; }

    public string Title { get; set; } = "Junk Drawer";

    public bool Visible { get; set; } = true;

    /// <summary>"Hide with Double Click": double-clicking empty desktop toggles Hide Desktop (see
    /// DesktopDoubleClickWatcher). Off by default.</summary>
    public bool HideOnDoubleClick { get; set; }
}

/// <summary>Same shape as LayoutLauncherStore, for the single JunkDrawerModel.</summary>
public sealed class JunkDrawerStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "junk-drawer.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public JunkDrawerModel Load()
    {
        if (!File.Exists(FilePath))
            return new JunkDrawerModel();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<JunkDrawerModel>(json, SerializerOptions) ?? new JunkDrawerModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new JunkDrawerModel();
        }
    }

    public void Save(JunkDrawerModel model)
    {
        Directory.CreateDirectory(DirectoryPath);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(model, SerializerOptions));
    }
}
