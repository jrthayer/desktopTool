using System.Text.Json;
using DesktopTool.UI;

namespace DesktopTool.Features.Wallpapers;

/// <summary>Persisted state for the on-screen Wallpapers widget itself (position, size, title,
/// visibility, plus the shared IWidgetStyle knobs from WidgetStyleModel). Each monitor's chosen
/// background lives separately in WallpaperSettings.</summary>
public sealed class WallpaperWidgetModel : WidgetStyleModel
{
    /// <summary>Null until the widget has been moved/resized once - see WallpaperWidget's own
    /// CreateParams, which centers it on the primary screen until then.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 320;
    public int? Height { get; set; }

    public string Title { get; set; } = "Wallpapers";

    public bool Visible { get; set; } = true;
}

/// <summary>Same shape as LayoutLauncherStore, for the single WallpaperWidgetModel.</summary>
public sealed class WallpaperWidgetStore
{
    private static readonly string FilePath = Path.Combine(WallpaperSettingsStore.DirectoryPath, "wallpaper-widget.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public WallpaperWidgetModel Load()
    {
        if (!File.Exists(FilePath))
            return new WallpaperWidgetModel();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<WallpaperWidgetModel>(json, SerializerOptions) ?? new WallpaperWidgetModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new WallpaperWidgetModel();
        }
    }

    public void Save(WallpaperWidgetModel model)
    {
        Directory.CreateDirectory(WallpaperSettingsStore.DirectoryPath);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(model, SerializerOptions));
    }
}
