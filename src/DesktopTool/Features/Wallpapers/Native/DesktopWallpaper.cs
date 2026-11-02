using System.Runtime.InteropServices;
using DesktopTool.Native;

namespace DesktopTool.Features.Wallpapers.Native;

/// <summary>How Windows sizes every monitor's wallpaper - a single system-wide setting (Windows has no
/// per-monitor fit). Values match DESKTOP_WALLPAPER_POSITION. Span is deliberately left out: it
/// stretches one image across every monitor, which would silently override every per-monitor
/// choice this feature makes.</summary>
public enum WallpaperFit
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
}

/// <summary>One monitor as the Shell's wallpaper API sees it. DeviceId is the stable device path
/// (survives reboots and rearranging), Bounds is in physical screen pixels - the same coordinate
/// space as Screen.Bounds for this PerMonitorV2 app.</summary>
public readonly record struct MonitorInfo(string DeviceId, Rectangle Bounds, string? CurrentImage);

/// <summary>Thin wrapper over the Shell's IDesktopWallpaper COM interface - the only Windows API
/// that sets a different image per monitor (SystemParametersInfo's SPI_SETDESKWALLPAPER only does
/// one image for everything). Each call creates and releases its own COM object, so nothing here
/// holds Explorer state between calls (an Explorer restart can't leave a dead reference behind).
/// Every COM failure surfaces as WallpaperException with a readable message.</summary>
public static class DesktopWallpaper
{
    public static IReadOnlyList<MonitorInfo> GetMonitors() => Use(wallpaper =>
    {
        var monitors = new List<MonitorInfo>();
        var count = wallpaper.GetMonitorDevicePathCount();
        for (uint i = 0; i < count; i++)
        {
            string id;
            RECT rect;
            try
            {
                id = wallpaper.GetMonitorDevicePathAt(i);
                // Throws for a monitor Windows still remembers but isn't currently attached.
                rect = wallpaper.GetMonitorRECT(id);
            }
            catch (COMException)
            {
                continue;
            }

            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                continue;

            string? image = null;
            try
            {
                image = wallpaper.GetWallpaper(id);
            }
            catch (COMException)
            {
                // A monitor showing a solid background color has no image - leave it null.
            }

            monitors.Add(new MonitorInfo(id,
                Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
                string.IsNullOrWhiteSpace(image) ? null : image));
        }
        return (IReadOnlyList<MonitorInfo>)monitors;
    });

    public static void SetImage(string deviceId, string imagePath) =>
        Use(wallpaper => { wallpaper.SetWallpaper(deviceId, imagePath); return 0; });

    public static WallpaperFit GetFit() => Use(wallpaper =>
    {
        var position = wallpaper.GetPosition();
        return Enum.IsDefined(typeof(WallpaperFit), position) ? (WallpaperFit)position : WallpaperFit.Fill;
    });

    public static void SetFit(WallpaperFit fit) =>
        Use(wallpaper => { wallpaper.SetPosition((int)fit); return 0; });

    private static T Use<T>(Func<IDesktopWallpaper, T> action)
    {
        IDesktopWallpaper? wallpaper = null;
        try
        {
            wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            return action(wallpaper);
        }
        catch (COMException ex)
        {
            throw new WallpaperException($"Windows couldn't change the wallpaper (0x{ex.HResult:X8}).", ex);
        }
        catch (InvalidCastException ex)
        {
            throw new WallpaperException("This version of Windows doesn't support per-monitor wallpapers.", ex);
        }
        finally
        {
            if (wallpaper is not null)
                Marshal.ReleaseComObject(wallpaper);
        }
    }

    [ComImport, Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperClass
    {
    }

    // Every method in vtable order - COM dispatches by slot, so skipping or reordering any of these
    // (even ones never called here) would call the wrong method.
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);

        uint GetMonitorDevicePathCount();

        RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

        void SetBackgroundColor(uint color);

        uint GetBackgroundColor();

        void SetPosition(int position);

        int GetPosition();

        void SetSlideshow(IntPtr items);

        IntPtr GetSlideshow();

        void SetSlideshowOptions(int options, uint slideshowTick);

        void GetSlideshowOptions(out int options, out uint slideshowTick);

        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, int direction);

        int GetStatus();

        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }
}

public sealed class WallpaperException : Exception
{
    public WallpaperException(string message, Exception inner) : base(message, inner)
    {
    }
}
