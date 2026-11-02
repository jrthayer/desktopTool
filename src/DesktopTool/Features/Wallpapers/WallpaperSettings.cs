using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopTool.Features.Wallpapers;

public enum BackgroundKind
{
    Image,
    Color,
    Slideshow,
}

/// <summary>What one monitor should be showing, as chosen through the Wallpapers widget. Only the
/// fields for the current Kind matter; the others are kept so switching back (say, from Color to a
/// slideshow) remembers the previous folder and interval.</summary>
public sealed class MonitorBackground
{
    public BackgroundKind Kind { get; set; } = BackgroundKind.Image;

    public string? ImagePath { get; set; }

    public int ColorArgb { get; set; } = unchecked((int)0xFF000000);

    public string? SlideshowFolder { get; set; }

    public int IntervalMinutes { get; set; } = 30;

    public bool Shuffle { get; set; }

    /// <summary>The slide currently on screen - where an in-order slideshow continues from, and what
    /// shuffle avoids repeating next.</summary>
    public string? CurrentSlide { get; set; }

    /// <summary>When the slideshow last changed this monitor's image - persisted so restarting the app
    /// waits out the rest of the interval instead of changing the image straight away.</summary>
    public DateTime? LastChangedUtc { get; set; }
}

/// <summary>Every monitor's MonitorBackground, keyed by IDesktopWallpaper's monitor device path
/// (stable across reboots). A monitor with no entry just shows whatever Windows already has.</summary>
public sealed class WallpaperSettings
{
    public Dictionary<string, MonitorBackground> Monitors { get; set; } = new();
}

/// <summary>Same shape as LayoutLauncherStore: a plain JSON file under %AppData%\DesktopTool, written
/// atomically, with a missing or corrupt file starting fresh.</summary>
public sealed class WallpaperSettingsStore
{
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTool");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "wallpapers.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public WallpaperSettings Load()
    {
        if (!File.Exists(FilePath))
            return new WallpaperSettings();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<WallpaperSettings>(json, SerializerOptions) ?? new WallpaperSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new WallpaperSettings();
        }
    }

    public void Save(WallpaperSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SerializerOptions));
    }
}
