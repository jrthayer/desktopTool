using Microsoft.Win32;

namespace DesktopTool;

/// <summary>
/// "Start with Windows" via the standard per-user Run key (HKCU, not HKLM) - no admin rights
/// needed, and it only affects the current user, matching a tray app that already only manages
/// this user's own desktop. IsEnabled treats the value's mere presence as authoritative rather
/// than persisting a separate on/off flag anywhere - the registry key already is the single
/// source of truth, so there's nothing to fall out of sync with it.
/// </summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopTool";

    /// Windows tracks a second, independent flag here for whether a Run-key entry is actually
    /// allowed to launch at logon - Task Manager's Startup tab and Settings > Apps > Startup write
    /// to this, not to the Run key itself. If a user ever disables DesktopTool from there instead of
    /// from this app, the Run key entry is left untouched but Windows silently stops launching it,
    /// while IsEnabled (which only checks the Run key) would still report it as on.
    private const string StartupApprovedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
    }

    /// <summary>Raised after SetEnabled, so every switch showing this setting (the Junk Drawer's, say)
    /// can repaint when another one changes it.</summary>
    public static event EventHandler? Changed;

    /// <summary>Environment.ProcessPath is the real DesktopTool.exe apphost, not dotnet.exe - a
    /// framework-dependent net8.0-windows WinExe still gets its own native apphost by default, so
    /// this is safe to point the Run key straight at without needing "dotnet DesktopTool.dll".
    /// Quoted since it could contain spaces (e.g. "Program Files").</summary>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (exePath is null)
                return;
            key.SetValue(ValueName, $"\"{exePath}\"");

            // Clear any stale "disabled" approval left over from a prior Task
            // Manager/Settings toggle - its absence is what a freshly-added Run entry
            // looks like, and Windows treats that as approved to launch.
            using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true);
            approvedKey?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }
}
