using System.Reflection;
using System.Runtime.InteropServices;

namespace DesktopTool.Features.Layouts.Native;

/// <summary>
/// File Explorer folder windows, for layouts: telling a real folder window apart from the rest of
/// explorer.exe's windows (taskbars, the desktop, shell chrome), reading which folder one is
/// showing, and turning that folder back into explorer.exe's launch argument.
/// </summary>
internal static class ExplorerWindows
{
    /// <summary>The window class of every File Explorer folder window - and nothing else explorer.exe
    /// owns, so it's what separates a capturable folder window from a taskbar.</summary>
    public const string FolderWindowClass = "CabinetWClass";

    /// <summary>The folder a File Explorer window is currently showing, found through the
    /// Shell.Application automation object (late-bound, the same way HiddenFilesManager uses it).
    /// Virtual folders (This PC, Home) come back as "::{GUID}" strings rather than a directory.
    /// With Windows 11's tabbed Explorer several tabs share one top-level window, and the first tab
    /// Shell.Application lists for it wins. Null if hWnd isn't a folder window or the lookup failed.</summary>
    public static string? TryGetFolderPath(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || Type.GetTypeFromProgID("Shell.Application") is not { } shellType)
            return null;

        object? shell = null;
        object? windows = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
            if (windows is null)
                return null;

            var windowsType = windows.GetType();
            var count = (int)windowsType.InvokeMember("Count", BindingFlags.GetProperty, null, windows, null)!;
            for (var i = 0; i < count; i++)
            {
                var path = TryGetPathIfMatches(windowsType, windows, i, hWnd);
                if (!string.IsNullOrEmpty(path))
                    return path;
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException)
        {
        }
        finally
        {
            Release(windows);
            Release(shell);
        }

        return null;
    }

    private static string? TryGetPathIfMatches(Type windowsType, object windows, int index, IntPtr hWnd)
    {
        object? window = null;
        object? document = null;
        object? folder = null;
        object? self = null;
        try
        {
            window = windowsType.InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { index });
            if (window is null)
                return null;

            var handle = window.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, window, null);
            if (handle is null || Convert.ToInt64(handle) != hWnd.ToInt64())
                return null;

            // Document is only a ShellFolderView for a folder window - an Internet Explorer-hosted
            // window (shouldn't exist on Win11, but Shell.Application still lists that kind) has
            // no Folder to read, and the reflection call just throws, which the catch below covers.
            document = window.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, window, null);
            folder = document?.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, document, null);
            self = folder?.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, folder, null);
            return self?.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, self, null) as string;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException
            or MissingMethodException or FormatException or OverflowException)
        {
            return null;
        }
        finally
        {
            Release(self);
            Release(folder);
            Release(document);
            Release(window);
        }
    }

    /// <summary>explorer.exe's argument for opening folderPath in a new window. A virtual folder's
    /// "::{GUID}" form gets the "shell:" prefix, which explorer.exe resolves reliably; a real path is
    /// just quoted. Empty for no folder, which opens Explorer's default view.</summary>
    public static string BuildLaunchArgs(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return string.Empty;

        var path = folderPath.Trim();
        if (path.StartsWith("::", StringComparison.Ordinal))
            path = "shell:" + path;

        return $"\"{path}\"";
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
    }
}
