using System.Drawing.Imaging;
using DesktopTool.Features.Wallpapers.Native;

namespace DesktopTool.Features.Wallpapers;

/// <summary>The one place that changes a monitor's background - the Wallpapers widget calls it, and
/// so does its own slideshow timer. Windows itself only does a separate *image* per monitor
/// (DesktopWallpaper.SetImage), so the other two kinds are built on top of that:
/// - Solid color: a PNG of that color at the monitor's own resolution, set as its image. Generated
///   once per color/size under %AppData%\DesktopTool\Wallpapers and reused after that.
/// - Slideshow: this class's own timer sets the next image from the folder whenever a monitor's
///   interval has passed. It only advances while the app is running.
/// Every change is saved to wallpapers.json and raises Changed.</summary>
public sealed class WallpaperService : IDisposable
{
    private static readonly string SolidColorDirectory = Path.Combine(WallpaperSettingsStore.DirectoryPath, "Wallpapers");

    private readonly WallpaperSettingsStore _store;
    private readonly WallpaperSettings _settings;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30_000 };
    private readonly Random _random = new();

    /// <summary>Raised after any background change this class makes, including a slideshow advance.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a slideshow advance fails (folder gone, Windows refused the image) - the
    /// widget shows it on the tile rather than as a popup, since this fires from a timer.</summary>
    public event EventHandler? SlideshowProblemsChanged;

    private readonly Dictionary<string, string> _slideshowProblems = new();

    public WallpaperService(WallpaperSettingsStore store)
    {
        _store = store;
        _settings = store.Load();
        _timer.Tick += (_, _) => AdvanceDueSlideshows();
    }

    /// <summary>Starts the slideshow timer, first catching up any monitor whose interval already ran
    /// out while the app was closed.</summary>
    public void Start()
    {
        AdvanceDueSlideshows();
        _timer.Start();
    }

    public MonitorBackground? GetBackground(string deviceId) =>
        _settings.Monitors.TryGetValue(deviceId, out var background) ? background : null;

    public string? GetSlideshowProblem(string deviceId) =>
        _slideshowProblems.TryGetValue(deviceId, out var problem) ? problem : null;

    public void ApplyImage(MonitorInfo monitor, string imagePath)
    {
        DesktopWallpaper.SetImage(monitor.DeviceId, imagePath);
        var background = GetOrCreate(monitor.DeviceId);
        background.Kind = BackgroundKind.Image;
        background.ImagePath = imagePath;
        ClearProblem(monitor.DeviceId);
        SaveAndNotify();
    }

    public void ApplyColor(MonitorInfo monitor, Color color)
    {
        var path = EnsureSolidColorImage(color, monitor.Bounds.Size);
        DesktopWallpaper.SetImage(monitor.DeviceId, path);
        var background = GetOrCreate(monitor.DeviceId);
        background.Kind = BackgroundKind.Color;
        background.ColorArgb = Color.FromArgb(255, color).ToArgb();
        ClearProblem(monitor.DeviceId);
        SaveAndNotify();
    }

    /// <summary>Switches this monitor to a slideshow of folder and shows its first image straight away
    /// (so picking a folder visibly does something). Throws WallpaperException if the folder has no
    /// images.</summary>
    public void StartSlideshow(MonitorInfo monitor, string folder)
    {
        var files = SlideshowPicker.ListImages(folder);
        if (files.Count == 0)
            throw new WallpaperException($"\"{folder}\" doesn't contain any images.", new FileNotFoundException());

        var background = GetOrCreate(monitor.DeviceId);
        background.Kind = BackgroundKind.Slideshow;
        background.SlideshowFolder = folder;
        background.CurrentSlide = null;
        ShowNext(monitor.DeviceId, background, files);
        SaveAndNotify();
    }

    /// <summary>Advances this monitor's slideshow now, restarting its interval.</summary>
    public void NextSlide(string deviceId)
    {
        if (GetBackground(deviceId) is not { Kind: BackgroundKind.Slideshow } background)
            return;
        ShowNext(deviceId, background, SlideshowPicker.ListImages(background.SlideshowFolder));
        SaveAndNotify();
    }

    public void SetInterval(string deviceId, int minutes)
    {
        var background = GetOrCreate(deviceId);
        background.IntervalMinutes = Math.Max(1, minutes);
        SaveAndNotify();
    }

    public void SetShuffle(string deviceId, bool shuffle)
    {
        var background = GetOrCreate(deviceId);
        background.Shuffle = shuffle;
        SaveAndNotify();
    }

    private void AdvanceDueSlideshows()
    {
        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var (deviceId, background) in _settings.Monitors)
        {
            if (background.Kind != BackgroundKind.Slideshow)
                continue;
            if (background.LastChangedUtc is { } last && now - last < TimeSpan.FromMinutes(Math.Max(1, background.IntervalMinutes)))
                continue;

            ShowNext(deviceId, background, SlideshowPicker.ListImages(background.SlideshowFolder));
            changed = true;
        }

        if (changed)
            SaveAndNotify();
    }

    /// <summary>Sets the next slide. A failure is recorded as this monitor's slideshow problem (shown
    /// on its tile) and still restarts the interval, so a broken folder is retried once per interval
    /// rather than on every 30-second tick.</summary>
    private void ShowNext(string deviceId, MonitorBackground background, IReadOnlyList<string> files)
    {
        background.LastChangedUtc = DateTime.UtcNow;

        var next = SlideshowPicker.Next(files, background.CurrentSlide, background.Shuffle, _random);
        if (next is null)
        {
            SetProblem(deviceId, $"No images found in \"{background.SlideshowFolder}\"");
            return;
        }

        try
        {
            DesktopWallpaper.SetImage(deviceId, next);
            background.CurrentSlide = next;
            ClearProblem(deviceId);
        }
        catch (WallpaperException ex)
        {
            SetProblem(deviceId, ex.Message);
        }
    }

    private static string EnsureSolidColorImage(Color color, Size size)
    {
        Directory.CreateDirectory(SolidColorDirectory);
        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        var path = Path.Combine(SolidColorDirectory, $"solid-{color.R:X2}{color.G:X2}{color.B:X2}-{width}x{height}.png");
        if (File.Exists(path))
            return path;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
            g.Clear(Color.FromArgb(255, color));

        // Written to a temp name first so a half-written file is never handed to Windows.
        var tempPath = path + ".tmp";
        bitmap.Save(tempPath, ImageFormat.Png);
        File.Move(tempPath, path, overwrite: true);
        return path;
    }

    private MonitorBackground GetOrCreate(string deviceId)
    {
        if (!_settings.Monitors.TryGetValue(deviceId, out var background))
        {
            background = new MonitorBackground();
            _settings.Monitors[deviceId] = background;
        }
        return background;
    }

    private void SetProblem(string deviceId, string message)
    {
        if (_slideshowProblems.TryGetValue(deviceId, out var existing) && existing == message)
            return;
        _slideshowProblems[deviceId] = message;
        SlideshowProblemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearProblem(string deviceId)
    {
        if (_slideshowProblems.Remove(deviceId))
            SlideshowProblemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveAndNotify()
    {
        _store.Save(_settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
