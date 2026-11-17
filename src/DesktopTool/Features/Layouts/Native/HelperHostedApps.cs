using DesktopTool.Features.Fences.Native;

namespace DesktopTool.Features.Layouts.Native;

/// <summary>
/// Apps whose visible windows belong to a helper process rather than the exe you launch. Steam is
/// the case that matters: since its 2023 client, every Steam window (main window, sign-in, friends)
/// is drawn by steamwebhelper.exe, a CEF helper that steam.exe spawns. Capturing a Steam window
/// therefore records steamwebhelper.exe, and starting that directly opens nothing.
///
/// Each entry maps the helper (the exe that owns the windows) to the launcher (the exe to start),
/// so capture can store the launcher and replay can launch the launcher while matching windows by
/// the helper. SingleInstance marks a launcher that only ever brings its existing window forward
/// when it's already running - WindowPlacer then falls back to placing that existing window.
/// </summary>
internal static class HelperHostedApps
{
    private sealed record App(
        string WindowExeName,
        string LauncherExeName,
        Func<string, string?> ResolveLauncherPath,
        bool SingleInstance,
        int LaunchTimeoutMs);

    private static readonly App[] Apps =
    {
        new("steamwebhelper.exe", "steam.exe", ResolveSteamExe, SingleInstance: true, LaunchTimeoutMs: 60000),
    };

    private static App? ByWindowExe(string exeName) =>
        Array.Find(Apps, a => string.Equals(a.WindowExeName, exeName, StringComparison.OrdinalIgnoreCase));

    private static App? ByLauncherExe(string exeName) =>
        Array.Find(Apps, a => string.Equals(a.LauncherExeName, exeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The exe to actually launch for programPath: the launcher if programPath is a known
    /// helper exe (and the launcher can be found), otherwise programPath unchanged.</summary>
    public static string ToLauncherPath(string programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath))
            return programPath;

        var app = ByWindowExe(Path.GetFileName(programPath));
        return app?.ResolveLauncherPath(programPath) ?? programPath;
    }

    /// <summary>The exe whose windows a launch of launcherExeName produces - the helper for a known
    /// launcher, otherwise launcherExeName itself.</summary>
    public static string WindowExeFor(string launcherExeName) =>
        ByLauncherExe(launcherExeName)?.WindowExeName ?? launcherExeName;

    public static bool IsSingleInstance(string launcherExeName) =>
        ByLauncherExe(launcherExeName)?.SingleInstance ?? false;

    /// <summary>How long to wait for this launcher's window, or null for WindowPlacer's default.</summary>
    public static int? TimeoutFor(string launcherExeName) =>
        ByLauncherExe(launcherExeName)?.LaunchTimeoutMs;

    /// <summary>steamwebhelper.exe lives a few levels under the Steam root
    /// (Steam\bin\cef\cef.win64\ today, other depths in older builds), so walk up looking for
    /// steam.exe, then fall back to the registry install path.</summary>
    private static string? ResolveSteamExe(string helperPath)
    {
        try
        {
            for (var dir = Path.GetDirectoryName(helperPath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                var candidate = Path.Combine(dir, "steam.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            // Malformed saved path - fall through to the registry.
        }

        if (GameLauncherProbe.SteamInstallPath() is { } root)
        {
            var candidate = Path.Combine(root.Replace('/', '\\'), "steam.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
