using System.Reflection;
using System.Runtime.InteropServices;
using DesktopTool.Features.Fences.Native;
using Microsoft.Win32;

namespace DesktopTool;

/// <summary>
/// Windows' own "Show hidden files, folders, and drives" Explorer folder option (View > Show >
/// Hidden items, or the same checkbox in Folder Options) - not anything specific to Fence Tool,
/// but exposed from the tray for convenience since fenced items live in a hidden folder (see
/// Native.DesktopIconHider) that's otherwise easy to forget how to reveal. IsEnabled treats the
/// registry value as authoritative rather than persisting a separate flag anywhere, the same
/// approach StartupManager uses for its own tray toggle.
/// </summary>
internal static class HiddenFilesManager
{
    private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "Hidden";
    private const int ShowHidden = 1;
    private const int HideHidden = 2; // Windows' own default

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: false);
            return key?.GetValue(ValueName) as int? == ShowHidden;
        }
    }

    /// <summary>Raised after SetEnabled, so every switch showing this setting (the Junk Drawer's, say)
    /// can repaint when another one changes it.</summary>
    public static event EventHandler? Changed;

    /// <summary>Windows' separate "Hide protected operating system files" option (Folder Options),
    /// stored as Advanced\ShowSuperHidden = 1 when they're shown. Off by default - Explorer keeps
    /// hiding Hidden+System files like desktop.ini even while hidden files are shown.</summary>
    public static bool ShowsProtectedSystemFiles
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: false);
            return key?.GetValue("ShowSuperHidden") as int? == 1;
        }
    }

    /// <summary>Whether Explorer would show an item with these attributes right now - the same two
    /// rules it applies: Hidden items only while Show Hidden Files is on, and Hidden+System items
    /// (protected OS files) only while protected files are shown too. For anything listing a real
    /// folder the way Explorer would (folder fences).</summary>
    public static bool IsShown(FileAttributes attributes, bool showHidden, bool showProtected)
    {
        if ((attributes & FileAttributes.Hidden) == 0)
            return true;
        if (!showHidden)
            return false;
        return (attributes & FileAttributes.System) == 0 || showProtected;
    }

    /// <summary>Changes the setting and makes it show up straight away - on the desktop and in every
    /// already-open File Explorer window - instead of only after an F5 in each. Writing the registry
    /// value alone changes the setting but not what's on screen; the steps after it are what make
    /// Explorer act on it (checked on Windows 11: the desktop's hidden hiddenDesktop folder appears
    /// and disappears, and an open Explorer window updates, with no manual refresh).
    ///
    /// The setting itself changes before this returns. Telling everyone about it happens on a
    /// background thread (see NotifyAndRefresh): the WM_SETTINGCHANGE broadcast waits on every open
    /// window on the system, up to a second each, so one busy app could otherwise freeze Desktop
    /// Tool (and, through the Junk Drawer's mouse hook, the mouse) for several seconds.</summary>
    public static void SetEnabled(bool enabled)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(AdvancedKeyPath))
        {
            key.SetValue(ValueName, enabled ? ShowHidden : HideHidden, RegistryValueKind.DWord);
        }

        // The shell's own API for this setting - updates Explorer's live copy of it, not just the
        // stored value. Masked to SSF_SHOWALLOBJECTS, so no other shell setting is touched.
        var state = new SHELLSTATE { Flags1 = enabled ? FShowAllObjects : 0 };
        SHGetSetSettings(ref state, SSF_SHOWALLOBJECTS, true);

        Changed?.Invoke(null, EventArgs.Empty);

        // STA because Shell.Application (RefreshExplorerWindows) is an apartment-threaded COM object.
        var worker = new Thread(NotifyAndRefresh) { IsBackground = true, Name = "Show Hidden Files refresh" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>Everything that makes Explorer redraw with the new setting - run off the UI thread
    /// (see SetEnabled). Each step reads nothing but the now-current shell state, so two quick
    /// toggles in a row can't leave the screen showing the wrong one.</summary>
    private static void NotifyAndRefresh()
    {
        // What Folder Options itself sends after changing this - tells every Explorer view the shell
        // state changed. The SHChangeNotify broadcast alone (all this used to send) wasn't enough.
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ShellState", SMTO_ABORTIFHUNG, 1000, out _);
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

        RefreshExplorerWindows();
        RefreshDesktop();
    }

    /// <summary>Refresh() on every open File Explorer window, found through the Shell.Application
    /// automation object. Late-bound so no interop assembly is needed; each window is refreshed on its
    /// own, so one closing mid-loop (or one that isn't a folder window) can't stop the rest.</summary>
    private static void RefreshExplorerWindows()
    {
        if (Type.GetTypeFromProgID("Shell.Application") is not { } shellType)
            return;

        object? shell = null;
        object? windows = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
            if (windows is null)
                return;

            var windowsType = windows.GetType();
            var count = (int)windowsType.InvokeMember("Count", BindingFlags.GetProperty, null, windows, null)!;
            for (var i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = windowsType.InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                    window?.GetType().InvokeMember("Refresh", BindingFlags.InvokeMethod, null, window, null);
                }
                catch (Exception ex) when (ex is COMException or TargetInvocationException)
                {
                }
                finally
                {
                    if (window is not null && Marshal.IsComObject(window))
                        Marshal.ReleaseComObject(window);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException)
        {
        }
        finally
        {
            if (windows is not null && Marshal.IsComObject(windows))
                Marshal.ReleaseComObject(windows);
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>The desktop isn't one of Shell.Application's windows, so its icon view gets the same
    /// command its own right-click > Refresh sends. On its own (with only the registry changed) this
    /// didn't help; after SHGetSetSettings/WM_SETTINGCHANGE above it does.</summary>
    private static void RefreshDesktop()
    {
        var (_, defView) = DesktopListView.FindDesktopDefView();
        if (defView != IntPtr.Zero)
            PostMessage(defView, WM_COMMAND, (IntPtr)FCIDM_SHVIEW_REFRESH, IntPtr.Zero);
    }

    // SHELLSTATE's native layout - only the first DWORD's bit 0 (fShowAllObjects) is ever used here,
    // but the struct has to be full size since SHGetSetSettings reads and writes the whole thing.
    [StructLayout(LayoutKind.Sequential)]
    private struct SHELLSTATE
    {
        public uint Flags1;
        public uint Win95Unused1;
        public uint Win95Unused2;
        public int ParamSort;
        public int SortDirection;
        public uint Version;
        public uint NotUsed;
        public uint Flags2;
    }

    private const uint FShowAllObjects = 0x1;
    private const uint SSF_SHOWALLOBJECTS = 0x1;
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_COMMAND = 0x0111;
    private const int FCIDM_SHVIEW_REFRESH = 0x7103;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("shell32.dll")]
    private static extern void SHGetSetSettings(ref SHELLSTATE state, uint mask, [MarshalAs(UnmanagedType.Bool)] bool set);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
