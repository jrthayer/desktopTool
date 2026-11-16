using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace DesktopTool.Features.Layouts.Native;

/// <summary>
/// Launches a Store/MSIX app from a captured exe path without ever using that path directly.
/// Capturing a packaged app's window yields something like
/// <c>C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24.12741.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe</c>
/// - a version-stamped folder Windows deletes on the app's next update. Even while it exists,
/// running the exe from there drops the app's package identity, which breaks many apps.
///
/// Instead, the package family (name + publisher id, both stable across updates) is pulled out of
/// that folder name, its current install is looked up, and the app is started the supported way:
/// its app execution alias (e.g. wt.exe) when the manifest declares one, otherwise activation by
/// AUMID through IApplicationActivationManager. Same spirit as SquirrelPathRepair, but nothing is
/// rewritten - the stored ProgramPath still names the exe that will own the window, which is what
/// WindowPlacer's window matching keys on.
/// </summary>
internal static class PackagedAppLauncher
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly string WindowsAppsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    private static readonly string AliasDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");

    /// <summary>Null if programPath isn't inside a WindowsApps package folder (the caller should
    /// launch it normally); true if the app was started; false if it is a packaged app but could
    /// not be started (package uninstalled, activation refused, ...).</summary>
    public static bool? TryLaunch(string programPath, string arguments)
    {
        if (!TrySplitPackagePath(programPath, out var familyName, out var relativeExe))
            return null;

        try
        {
            var installDir = FindInstallDirectory(familyName, relativeExe);
            if (installDir is null)
                return false;

            var (appId, aliases) = ReadApplication(Path.Combine(installDir, "AppxManifest.xml"), relativeExe);
            if (appId is null)
                return false;

            foreach (var alias in aliases)
            {
                var aliasPath = Path.Combine(AliasDirectory, alias);
                if (!File.Exists(aliasPath))
                    continue;

                Process.Start(new ProcessStartInfo(aliasPath) { Arguments = arguments, UseShellExecute = true });
                return true;
            }

            return Activate($"{familyName}!{appId}", arguments);
        }
        catch (Exception ex) when (ex is Win32Exception or COMException or IOException
            or UnauthorizedAccessException or System.Xml.XmlException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>Package folders are named Name_Version_Arch_ResourceId_PublisherId (ResourceId is
    /// usually empty, hence the "__"). Package names can't contain '_', so the family name -
    /// Name_PublisherId - is simply the first and last '_'-separated parts.</summary>
    private static bool TrySplitPackagePath(string programPath, out string familyName, out string relativeExe)
    {
        familyName = relativeExe = string.Empty;
        if (string.IsNullOrWhiteSpace(programPath) || !Path.IsPathFullyQualified(programPath))
            return false;

        var root = WindowsAppsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(programPath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = full[root.Length..];
        var slash = rest.IndexOf(Path.DirectorySeparatorChar);
        if (slash <= 0 || slash == rest.Length - 1)
            return false;

        var parts = rest[..slash].Split('_');
        if (parts.Length < 5 || parts[0].Length == 0 || parts[^1].Length == 0)
            return false;

        familyName = $"{parts[0]}_{parts[^1]}";
        relativeExe = rest[(slash + 1)..];
        return true;
    }

    /// <summary>The current install folder of the package family - whatever version is installed
    /// now - preferring one that actually contains relativeExe if several (e.g. per-architecture)
    /// are registered.</summary>
    private static string? FindInstallDirectory(string familyName, string relativeExe)
    {
        var dirs = GetPackageFullNames(familyName)
            .Select(GetPackagePath)
            .OfType<string>()
            .ToList();

        return dirs.Find(d => File.Exists(Path.Combine(d, relativeExe))) ?? dirs.FirstOrDefault();
    }

    private static List<string> GetPackageFullNames(string familyName)
    {
        uint count = 0, bufferLength = 0;
        var result = GetPackagesByPackageFamily(familyName, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero);
        if (result != ErrorInsufficientBuffer || count == 0)
            return new List<string>();

        var namePointers = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
        var buffer = Marshal.AllocHGlobal(sizeof(char) * (int)bufferLength);
        try
        {
            if (GetPackagesByPackageFamily(familyName, ref count, namePointers, ref bufferLength, buffer) != ErrorSuccess)
                return new List<string>();

            var names = new List<string>((int)count);
            for (var i = 0; i < count; i++)
            {
                var ptr = Marshal.ReadIntPtr(namePointers, i * IntPtr.Size);
                if (Marshal.PtrToStringUni(ptr) is { Length: > 0 } name)
                    names.Add(name);
            }

            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(namePointers);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? GetPackagePath(string fullName)
    {
        uint length = 0;
        if (GetPackagePathByFullName(fullName, ref length, null) != ErrorInsufficientBuffer || length == 0)
            return null;

        var buffer = new char[length];
        if (GetPackagePathByFullName(fullName, ref length, buffer) != ErrorSuccess)
            return null;

        var path = new string(buffer, 0, (int)Math.Max(0, length - 1));
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>The Application Id whose Executable matches relativeExe (falling back to the first
    /// Application if none does - an app whose window comes from a helper exe, say), plus any app
    /// execution aliases that Application declares. Matched by local name so the many versioned
    /// manifest namespaces (uap3:, desktop:, ...) don't matter.</summary>
    private static (string? AppId, List<string> Aliases) ReadApplication(string manifestPath, string relativeExe)
    {
        var doc = XDocument.Load(manifestPath);
        var applications = doc.Descendants().Where(e => e.Name.LocalName == "Application").ToList();

        var app = applications.Find(a => string.Equals(
                ((string?)a.Attribute("Executable"))?.Replace('/', '\\'), relativeExe, StringComparison.OrdinalIgnoreCase))
            ?? applications.FirstOrDefault();
        if (app is null)
            return (null, new List<string>());

        var aliases = app.Descendants()
            .Where(e => e.Name.LocalName == "ExecutionAlias")
            .Select(e => (string?)e.Attribute("Alias"))
            .OfType<string>()
            .Where(a => a.Length > 0)
            .ToList();

        return ((string?)app.Attribute("Id"), aliases);
    }

    private static bool Activate(string appUserModelId, string arguments)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            var hr = manager.ActivateApplication(appUserModelId,
                string.IsNullOrEmpty(arguments) ? null : arguments, ActivateOptions.None, out _);
            return hr >= 0;
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count,
        IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength,
        [Out] char[]? path);

    private enum ActivateOptions
    {
        None = 0,
    }

    // Only ActivateApplication is called; it's the first method in the vtable, so the remaining
    // members (ActivateForFile, ActivateForProtocol) don't need declaring.
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, ActivateOptions options, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
    }
}
