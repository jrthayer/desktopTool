using DesktopTool.Native;
using Microsoft.Win32;

namespace DesktopTool.Features.WindowSnapping.Native;

/// <summary>Turns off the parts of Windows' own snapping that fight WindowSnapper, for as long as
/// this exists:
///
/// - Snap sizing: resizing a window's top or bottom edge to the edge of the screen stretches it to
///   full height, throwing away the size being dragged to and breaking a shared top/bottom edge.
/// - Dock moving: dragging a window to the top edge maximizes it, and to the left/right edge
///   half-snaps it. Windows has no separate switch for the top edge alone.
///
/// Win+Arrow and Snap Layouts are left alone.
///
/// Set with SPIF_SENDCHANGE alone, never SPIF_UPDATEINIFILE, so the change only lives in the current
/// session and the user's own settings stay saved in the registry untouched. Dispose puts back
/// whatever the registry says rather than what the live value was at construction: if an earlier
/// run was killed without disposing, the live value is already off when this starts, and
/// remembering that would leave it off for good.</summary>
internal sealed class WindowsSnapOverride : IDisposable
{
    private sealed record Setting(uint Get, uint Set, string RegistryValue);

    private static readonly Setting[] Settings =
    {
        new(NativeMethods.SPI_GETSNAPSIZING, NativeMethods.SPI_SETSNAPSIZING, "SnapSizing"),
        new(NativeMethods.SPI_GETDOCKMOVING, NativeMethods.SPI_SETDOCKMOVING, "DockMoving"),
    };

    private const string DesktopKey = @"Control Panel\Desktop";
    private bool _disposed;

    public WindowsSnapOverride()
    {
        foreach (var setting in Settings)
        {
            if (NativeMethods.SystemParametersInfo(setting.Get, 0, out int enabled, 0) && enabled != 0)
                Apply(setting, false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var setting in Settings)
        {
            if (SavedSetting(setting))
                Apply(setting, true);
        }
    }

    /// <summary>The user's own setting, as Windows saved it. Windows treats a missing value as on.</summary>
    private static bool SavedSetting(Setting setting)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
            return key?.GetValue(setting.RegistryValue) is not string value || value != "0";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void Apply(Setting setting, bool enabled) =>
        NativeMethods.SystemParametersInfo(setting.Set, enabled ? 1u : 0u, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE);
}
