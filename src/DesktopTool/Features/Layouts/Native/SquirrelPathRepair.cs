using System.Text.RegularExpressions;

namespace DesktopTool.Features.Layouts.Native;

/// <summary>
/// Repairs a launch path that has gone stale because a Squirrel.Windows app reinstalled itself
/// into a new versioned folder. Discord is the common offender (it updates often), but Slack,
/// GitHub Desktop, WhatsApp, 1Password, Teams classic and others use the same layout:
/// <c>&lt;base&gt;\app-&lt;version&gt;\&lt;exe&gt;</c>, with an <c>Update.exe</c> sibling next to the app-* folders.
/// An update drops a fresh <c>app-&lt;version&gt;</c> alongside and deletes the old one, so a path
/// captured beforehand points at a folder that no longer exists with everything except that one
/// segment unchanged.
/// </summary>
internal static class SquirrelPathRepair
{
    // "app-1.0.9255", "app-4.2.0", "app-1.2.3-beta2" - a leading numeric version with an optional
    // pre-release tail. Matched against a whole path segment.
    private static readonly Regex VersionFolder = new(@"^app-\d+(\.\d+)*([-.].*)?$", RegexOptions.IgnoreCase);

    /// <summary>The current path for the same executable under the newest sibling <c>app-*</c>
    /// folder, or null if <paramref name="programPath"/> isn't a Squirrel-shaped path, still exists,
    /// has no surviving base folder, or no sibling <c>app-*</c> folder actually contains the same
    /// executable. A pure lookup - it never mutates anything and only touches disk for existence
    /// checks and one directory enumeration.</summary>
    public static string? TryRepair(string programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath) || !Path.IsPathFullyQualified(programPath))
            return null;
        if (File.Exists(programPath))
            return null;

        var segments = programPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var versionIndex = Array.FindIndex(segments, s => VersionFolder.IsMatch(s));
        if (versionIndex <= 0 || versionIndex == segments.Length - 1)
            return null;

        var baseDir = string.Join(Path.DirectorySeparatorChar, segments[..versionIndex]);
        var tail = Path.Combine(segments[(versionIndex + 1)..]);
        if (!Directory.Exists(baseDir))
            return null;

        string[] siblings;
        try
        {
            siblings = Directory.GetDirectories(baseDir, "app-*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var best = siblings
            .Where(dir => VersionFolder.IsMatch(Path.GetFileName(dir)))
            .Select(dir => new { Dir = dir, Exe = Path.Combine(dir, tail) })
            .Where(c => File.Exists(c.Exe))
            .OrderByDescending(c => ParseVersion(Path.GetFileName(c.Dir)))
            .ThenByDescending(c => SafeWriteTimeUtc(c.Dir))
            .FirstOrDefault();

        if (best is null || PathsEqual(best.Exe, programPath))
            return null;
        return best.Exe;
    }

    private static Version ParseVersion(string folderName)
    {
        var digits = Regex.Match(folderName, @"\d+(\.\d+)*");
        // Version needs at least major.minor - "app-9255" -> "9255.0".
        var text = digits.Success ? digits.Value : "0.0";
        if (!text.Contains('.'))
            text += ".0";
        return Version.TryParse(text, out var v) ? v : new Version(0, 0);
    }

    private static DateTime SafeWriteTimeUtc(string dir)
    {
        try { return Directory.GetLastWriteTimeUtc(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
