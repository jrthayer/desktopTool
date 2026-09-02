using System.Runtime.InteropServices;
using System.Text;

namespace DesktopTool.Features.Layouts.Native;

/// <summary>
/// Resolves a .lnk shortcut's own target executable (and command-line arguments), via the standard
/// IShellLink/IPersistFile COM pair rather than a NuGet dependency - needed because WindowPlacer
/// has to know the *actual* exe name to watch for (a shortcut's own file name routinely has nothing
/// to do with its target, e.g. "Google Chrome.lnk" -> chrome.exe), and matching on the shortcut's
/// own name would silently never find the window it launches. The arguments matter for shortcuts
/// that launch a shared host exe and pick what to run via a flag - e.g. a Riot game shortcut always
/// targets RiotClientServices.exe and names the game only in --launch-product (see GameLauncherProbe).
///
/// The interface below declares every vtable slot up to and including the last method actually
/// called (GetArguments) - COM dispatch is purely positional, so a method after the ones you call
/// doesn't need declaring, but every one before them does, even unused.
/// </summary>
internal static class ShortcutResolver
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(StringBuilder pszArgs, int cchMaxPath);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }

    private const uint STGM_READ = 0;

    /// <summary>A shortcut's resolved target and its command-line arguments. Target is null when the
    /// shortcut has no filesystem-path target (a shell-namespace or UWP-app link); Arguments is
    /// never null, just possibly empty.</summary>
    public sealed record ShortcutInfo(string? Target, string Arguments);

    /// <summary>Null if lnkPath isn't a real/readable shortcut - callers fall back to the
    /// shortcut's own path in that case (see WindowPlacer.ResolveExeName).</summary>
    public static string? ResolveTarget(string lnkPath) => Resolve(lnkPath)?.Target;

    /// <summary>Null if lnkPath isn't a real/readable shortcut.</summary>
    public static ShortcutInfo? Resolve(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(lnkPath, STGM_READ);

            var pathBuffer = new StringBuilder(260);
            link.GetPath(pathBuffer, pathBuffer.Capacity, IntPtr.Zero, 0);
            var target = pathBuffer.ToString();

            var argsBuffer = new StringBuilder(1024);
            link.GetArguments(argsBuffer, argsBuffer.Capacity);

            return new ShortcutInfo(string.IsNullOrEmpty(target) ? null : target, argsBuffer.ToString());
        }
        catch (COMException)
        {
            return null;
        }
    }
}
