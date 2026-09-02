using System.Text.RegularExpressions;
using DesktopTool.Features.Layouts.Native;
using Microsoft.Win32;

namespace DesktopTool.Features.Fences.Native;

/// <summary>
/// Detects fence shortcuts that launch a game through Steam or the Riot Client where that game has
/// since been uninstalled. Both launchers are shared: the launcher exe (and, for Steam, the .url
/// shortcut file itself) survive a game uninstall untouched, and a Riot game shortcut always
/// targets the same RiotClientServices.exe no matter which game it starts - so the ordinary "does
/// the shortcut's own target still exist" test (FenceManager.PruneDeadItems) can never see the
/// game go away.
///
/// Every check here is deliberately one-sided: it returns true only on a confident negative -
/// launcher located, shortcut parsed, game demonstrably absent. Anything unrecognised or
/// unreadable (Steam not installed, a plain web .url, an unparseable shortcut, an unexpected
/// metadata format) returns false, so a fence item is only ever pruned when we're sure.
/// </summary>
internal static class GameLauncherProbe
{
    public static bool LaunchesUninstalledGame(string shortcutPath)
    {
        var ext = Path.GetExtension(shortcutPath);
        if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return SteamGameUninstalled(shortcutPath);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return RiotGameUninstalled(shortcutPath);
        return false;
    }

    // --- Steam ---------------------------------------------------------------------------------

    /// <summary>A Steam game .url carries no filesystem target, only "URL=steam://rungameid/&lt;appid&gt;".
    /// A game is installed iff some Steam library folder has a steamapps\appmanifest_&lt;appid&gt;.acf -
    /// libraryfolders.vdf's own "apps" list is NOT reliably pruned on uninstall, the manifest file is.</summary>
    private static bool SteamGameUninstalled(string urlFilePath)
    {
        string text;
        try { text = File.ReadAllText(urlFilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        var id = Regex.Match(text, @"steam://(?:rungameid|run)/(\d+)", RegexOptions.IgnoreCase);
        if (!id.Success)
            return false; // not a Steam game shortcut (a plain web bookmark .url, etc.)

        var steamRoot = SteamInstallPath();
        if (steamRoot is null)
            return false; // Steam not found - can't judge

        var appId = id.Groups[1].Value;
        foreach (var library in SteamLibraryPaths(steamRoot))
        {
            try
            {
                if (File.Exists(Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf")))
                    return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        return true;
    }

    private static string? SteamInstallPath()
    {
        static string? FromKey(RegistryKey root, string subKey, string valueName)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                return key?.GetValue(valueName) is string p && Directory.Exists(p) ? p : null;
            }
            catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return FromKey(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
            ?? FromKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
    }

    private static IEnumerable<string> SteamLibraryPaths(string steamRoot)
    {
        yield return steamRoot;

        // Modern Steam keeps this under steamapps\; older builds kept it under config\.
        foreach (var relative in new[] { @"steamapps\libraryfolders.vdf", @"config\libraryfolders.vdf" })
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(steamRoot, relative)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            // "path"  "D:\\SteamLibrary"  (new format) - the older "1" "D:\\SteamLibrary" numbered
            // form is matched by the second pattern's drive-letter shape.
            foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
                yield return m.Groups[1].Value.Replace(@"\\", @"\");
            foreach (Match m in Regex.Matches(text, "\"\\d+\"\\s*\"([A-Za-z]:[^\"]+)\""))
                yield return m.Groups[1].Value.Replace(@"\\", @"\");
        }
    }

    // --- Riot Client -------------------------------------------------------------------------------

    /// <summary>A Riot game .lnk always targets RiotClientServices.exe and names the game only in
    /// its arguments (--launch-product=&lt;p&gt; --launch-patchline=&lt;pl&gt;). Riot records where each
    /// product installs in %ProgramData%\Riot Games\Metadata\&lt;p&gt;.&lt;pl&gt;\&lt;p&gt;.&lt;pl&gt;.product_settings.yaml
    /// (key product_install_full_path) - if that directory is gone, the game is uninstalled even
    /// though the launcher, the shortcut, and the stale metadata folder all remain.</summary>
    private static bool RiotGameUninstalled(string lnkPath)
    {
        var info = ShortcutResolver.Resolve(lnkPath);
        if (info?.Target is null ||
            !Path.GetFileName(info.Target).Equals("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        var product = Regex.Match(info.Arguments, @"--launch-product=([^\s""]+)", RegexOptions.IgnoreCase);
        if (!product.Success)
            return false; // the plain "Riot Client" shortcut - no particular game to be missing

        var patchline = Regex.Match(info.Arguments, @"--launch-patchline=([^\s""]+)", RegexOptions.IgnoreCase);
        var slug = $"{product.Groups[1].Value}.{(patchline.Success ? patchline.Groups[1].Value : "live")}";

        var settingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games", "Metadata", slug, $"{slug}.product_settings.yaml");
        if (!File.Exists(settingsFile))
            return false; // no metadata to read - don't guess

        string yaml;
        try { yaml = File.ReadAllText(settingsFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        var installPath = Regex.Match(yaml, @"product_install_full_path:\s*""([^""]+)""");
        if (!installPath.Success)
            return false; // unexpected format

        return !Directory.Exists(installPath.Groups[1].Value.Replace('/', '\\'));
    }
}
