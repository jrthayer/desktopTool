using System.Runtime.InteropServices;
using System.Text;
using DesktopTool.Native;

namespace DesktopTool.Features.Fences.Native;

public sealed record DesktopIcon(int Index, string Label, Point Position);

/// <summary>
/// Locates the desktop's icon SysListView32 (owned by explorer.exe) and reads its contents.
/// Handle discovery must be re-run whenever explorer.exe restarts, so callers should treat
/// a failed read as a signal to call <see cref="EnsureDiscovered"/> again rather than a fatal error.
/// </summary>
public sealed class DesktopListView : IDisposable
{
    private readonly BackgroundMessageWindow _messageWindow;

    private IntPtr _hAnchor;
    private IntPtr _hDefView;
    private IntPtr _hListView;
    private uint _listViewProcessId;

    public DesktopListView()
    {
        _messageWindow = new BackgroundMessageWindow();
        _messageWindow.TaskbarCreated += (_, _) =>
        {
            Invalidate();
            ExplorerRestarted?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Fires after explorer.exe restarts, once handles have been invalidated. Callers
    /// that cached the anchor/DefView handles (e.g. to re-parent a window onto the desktop)
    /// should re-anchor in response, since those handles are now stale.</summary>
    public event EventHandler? ExplorerRestarted;

    /// <summary>Fires when a cross-process operation fails because explorer.exe is running at a
    /// different integrity level than this process (e.g. elevated) - no retry will fix this on
    /// its own, so callers should surface it to the user instead of silently doing nothing.</summary>
    public event EventHandler? AccessDenied;

    public bool IsDiscovered => _hListView != IntPtr.Zero && NativeMethods.IsWindow(_hListView);

    public bool EnsureDiscovered() => IsDiscovered || Discover();

    /// <summary>The Progman or WorkerW window that directly parents SHELLDLL_DefView - the
    /// window a fence should be SetParent'd onto to live on the desktop.</summary>
    public IntPtr AnchorHandle => _hAnchor;

    /// <summary>SHELLDLL_DefView - passing this as SetWindowPos's hwndInsertAfter places a
    /// fence immediately behind the icon layer in z-order.</summary>
    public IntPtr DefViewHandle => _hDefView;

    public void Dispose() => _messageWindow.Dispose();

    /// <summary>Forces the desktop's icon list to repaint immediately rather than whenever its own
    /// next natural paint cycle happens to land - SHChangeNotify alone (see DesktopIconHider) tells
    /// Explorer that an item's attributes changed, but doesn't guarantee it repaints the affected
    /// area right away, which is what actually made a hide/restore visibly lag in practice.</summary>
    public void ForceRepaint()
    {
        if (!EnsureDiscovered())
            return;

        NativeMethods.InvalidateRect(_hListView, IntPtr.Zero, true);
        NativeMethods.UpdateWindow(_hListView);
    }


    private void Invalidate()
    {
        _hAnchor = _hDefView = _hListView = IntPtr.Zero;
        _listViewProcessId = 0;
    }

    /// <summary>OpenProcess against explorer.exe can fail for reasons that clear up on their own
    /// (explorer mid-restart) or ones that won't (a real integrity-level mismatch) - only the
    /// latter is worth telling the user about, so this checks which one actually happened.</summary>
    private void NotifyOpenProcessFailed()
    {
        var error = Marshal.GetLastWin32Error();
        Invalidate();
        if (error == NativeMethods.ERROR_ACCESS_DENIED)
            AccessDenied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Walks Progman -> (WorkerW ->) SHELLDLL_DefView -> SysListView32. Windows 11 24H2+ puts
    /// SHELLDLL_DefView directly under Progman again, which the direct-child check below already
    /// covers; the WorkerW enumeration is the pre-24H2 fallback for wallpaper-slideshow/multi-monitor setups.
    /// </summary>
    private bool Discover()
    {
        var (anchor, hDefView) = FindDesktopDefView();
        if (hDefView == IntPtr.Zero)
            return false;

        var hListView = NativeMethods.FindWindowEx(hDefView, IntPtr.Zero, "SysListView32", null);
        if (hListView == IntPtr.Zero)
            return false;

        _hAnchor = anchor;
        _hDefView = hDefView;
        _hListView = hListView;
        NativeMethods.GetWindowThreadProcessId(_hListView, out _listViewProcessId);
        DisableAutoArrange();
        return true;
    }

    /// <summary>
    /// Clears auto-arrange/snap-to-grid so manually-set icon positions stick. This only affects
    /// the live window style, not the user's persisted view settings, so it's reasserted here on
    /// every (re)discovery rather than written to the registry - explorer restarts or the user
    /// re-enabling it from the desktop's View menu will simply get cleared again next time we reconnect.
    /// </summary>
    private void DisableAutoArrange()
    {
        var style = NativeMethods.GetWindowLongPtr(_hListView, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~NativeMethods.LVS_AUTOARRANGE;
        NativeMethods.SetWindowLongPtr(_hListView, NativeMethods.GWL_STYLE, (IntPtr)style);

        NativeMethods.SendMessage(_hListView, NativeMethods.LVM_SETEXTENDEDLISTVIEWSTYLE,
            (IntPtr)(NativeMethods.LVS_EX_SNAPTOGRID | NativeMethods.LVS_EX_AUTOAUTOARRANGE), IntPtr.Zero);
    }

    /// <summary>The desktop's SHELLDLL_DefView (the shell view hosting the icons) and the window that
    /// directly parents it - Progman on Windows 11 24H2+, or the icon-hosting WorkerW before that.
    /// Both zero if Explorer isn't running. Static so code that only needs to message the desktop's
    /// view (HiddenFilesManager's refresh, say) can find it without a DesktopListView of its own.</summary>
    internal static (IntPtr Anchor, IntPtr DefView) FindDesktopDefView()
    {
        var hProgman = NativeMethods.FindWindow("Progman", null);
        if (hProgman == IntPtr.Zero)
            return (IntPtr.Zero, IntPtr.Zero);

        // Nudges Explorer to create the icon-hosting WorkerW if it hasn't already.
        NativeMethods.SendMessageTimeout(hProgman, NativeMethods.WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, 1000, out _);

        var hDefView = NativeMethods.FindWindowEx(hProgman, IntPtr.Zero, "SHELLDLL_DefView", null);
        return hDefView != IntPtr.Zero ? (hProgman, hDefView) : FindDefViewUnderWorkerW();
    }

    private static (IntPtr anchor, IntPtr defView) FindDefViewUnderWorkerW()
    {
        IntPtr foundAnchor = IntPtr.Zero;
        IntPtr foundDefView = IntPtr.Zero;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (GetClassName(hWnd) != "WorkerW")
                return true;

            var candidate = NativeMethods.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (candidate == IntPtr.Zero)
                return true; // this WorkerW hosts the wallpaper, not the icons - keep looking

            foundAnchor = hWnd;
            foundDefView = candidate;
            return false;
        }, IntPtr.Zero);

        return (foundAnchor, foundDefView);
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public IReadOnlyList<DesktopIcon> EnumerateIcons()
    {
        if (!EnsureDiscovered())
            return Array.Empty<DesktopIcon>();

        int count = (int)NativeMethods.SendMessage(_hListView, NativeMethods.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0)
            return Array.Empty<DesktopIcon>();

        var hProcess = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ |
            NativeMethods.PROCESS_VM_WRITE | NativeMethods.PROCESS_QUERY_INFORMATION,
            false, _listViewProcessId);

        if (hProcess == IntPtr.Zero)
        {
            NotifyOpenProcessFailed();
            return Array.Empty<DesktopIcon>();
        }

        const int textBufferChars = 260;
        var lvItemSize = Marshal.SizeOf<LVITEM>();
        var textBufferSize = textBufferChars * sizeof(char);
        var pointSize = Marshal.SizeOf<POINT>();

        var remoteLvItem = IntPtr.Zero;
        var remoteText = IntPtr.Zero;
        var remotePoint = IntPtr.Zero;

        try
        {
            remoteLvItem = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero, (uint)lvItemSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
            remoteText = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero, (uint)textBufferSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
            remotePoint = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero, (uint)pointSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);

            if (remoteLvItem == IntPtr.Zero || remoteText == IntPtr.Zero || remotePoint == IntPtr.Zero)
                return Array.Empty<DesktopIcon>();

            var origin = GetListViewOrigin();
            var results = new List<DesktopIcon>(count);
            for (int i = 0; i < count; i++)
            {
                var label = ReadItemText(hProcess, i, remoteLvItem, remoteText, textBufferChars, lvItemSize);
                var position = ReadItemPosition(hProcess, i, remotePoint, pointSize);
                results.Add(new DesktopIcon(i, label, new Point(position.X + origin.X, position.Y + origin.Y)));
            }

            return results;
        }
        finally
        {
            if (remoteLvItem != IntPtr.Zero) NativeMethods.VirtualFreeEx(hProcess, remoteLvItem, 0, NativeMethods.MEM_RELEASE);
            if (remoteText != IntPtr.Zero) NativeMethods.VirtualFreeEx(hProcess, remoteText, 0, NativeMethods.MEM_RELEASE);
            if (remotePoint != IntPtr.Zero) NativeMethods.VirtualFreeEx(hProcess, remotePoint, 0, NativeMethods.MEM_RELEASE);
            NativeMethods.CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Moves the given icons (by their current list index) to new positions in one batch,
    /// reusing a single remote POINT buffer and process handle across all writes.
    /// </summary>
    public bool SetItemPositions(IReadOnlyList<(int Index, Point Position)> placements)
    {
        if (placements.Count == 0)
            return true;

        if (!EnsureDiscovered())
            return false;

        var hProcess = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_WRITE | NativeMethods.PROCESS_QUERY_INFORMATION,
            false, _listViewProcessId);

        if (hProcess == IntPtr.Zero)
        {
            NotifyOpenProcessFailed();
            return false;
        }

        var pointSize = Marshal.SizeOf<POINT>();
        var remotePoint = IntPtr.Zero;

        try
        {
            remotePoint = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero, (uint)pointSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
            if (remotePoint == IntPtr.Zero)
                return false;

            var origin = GetListViewOrigin();
            foreach (var (index, position) in placements)
            {
                var point = new POINT { X = position.X - origin.X, Y = position.Y - origin.Y };
                var bytes = StructToBytes(point, pointSize);
                if (!NativeMethods.WriteProcessMemory(hProcess, remotePoint, bytes, (uint)pointSize, out _))
                    continue;

                NativeMethods.SendMessage(_hListView, NativeMethods.LVM_SETITEMPOSITION32, (IntPtr)index, remotePoint);

                // LVM_SETITEMPOSITION32 updates the ListView's internal bookkeeping but doesn't
                // reliably repaint the affected screen area on its own (unlike a real user drag,
                // which has its own visual feedback) - without this, the icon logically moves
                // (reads back correctly) but visually stays put or renders nowhere until
                // something else forces a repaint. LVM_REDRAWITEMS targets the specific item
                // (unlike a blanket InvalidateRect, which alone wasn't enough).
                NativeMethods.SendMessage(_hListView, NativeMethods.LVM_REDRAWITEMS, (IntPtr)index, (IntPtr)index);
            }

            NativeMethods.InvalidateRect(_hListView, IntPtr.Zero, true);
            NativeMethods.UpdateWindow(_hListView);

            return true;
        }
        finally
        {
            if (remotePoint != IntPtr.Zero)
                NativeMethods.VirtualFreeEx(hProcess, remotePoint, 0, NativeMethods.MEM_RELEASE);
            NativeMethods.CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// LVM_GETITEMPOSITION/LVM_SETITEMPOSITION32 use coordinates relative to the listview's own
    /// client area, which starts at the top-left of the *virtual* screen (spanning all monitors) -
    /// not at (0, 0). On a multi-monitor setup where another monitor sits left of or above the
    /// primary one, that origin is a negative screen coordinate. Everything else in this app (fence
    /// bounds, via GetWindowRect) works in absolute screen coordinates, so reads/writes here must
    /// translate through this origin or icons land shifted by exactly that monitor's offset.
    /// </summary>
    private Point GetListViewOrigin() =>
        NativeMethods.GetWindowRect(_hListView, out var rect) ? new Point(rect.Left, rect.Top) : Point.Empty;

    private string ReadItemText(IntPtr hProcess, int index, IntPtr remoteLvItem, IntPtr remoteText,
        int textBufferChars, int lvItemSize)
    {
        var lvItem = new LVITEM
        {
            mask = NativeMethods.LVIF_TEXT,
            iItem = index,
            pszText = remoteText,
            cchTextMax = textBufferChars,
        };

        if (!NativeMethods.WriteProcessMemory(hProcess, remoteLvItem, StructToBytes(lvItem, lvItemSize), (uint)lvItemSize, out _))
            return string.Empty;

        NativeMethods.SendMessage(_hListView, NativeMethods.LVM_GETITEMTEXTW, (IntPtr)index, remoteLvItem);

        var textBytes = new byte[textBufferChars * sizeof(char)];
        if (!NativeMethods.ReadProcessMemory(hProcess, remoteText, textBytes, (uint)textBytes.Length, out _))
            return string.Empty;

        var text = Encoding.Unicode.GetString(textBytes);
        var nullIndex = text.IndexOf('\0');
        return nullIndex >= 0 ? text[..nullIndex] : text;
    }

    private Point ReadItemPosition(IntPtr hProcess, int index, IntPtr remotePoint, int pointSize)
    {
        NativeMethods.SendMessage(_hListView, NativeMethods.LVM_GETITEMPOSITION, (IntPtr)index, remotePoint);

        var bytes = new byte[pointSize];
        if (!NativeMethods.ReadProcessMemory(hProcess, remotePoint, bytes, (uint)bytes.Length, out _))
            return Point.Empty;

        var point = BytesToStruct<POINT>(bytes);
        return new Point(point.X, point.Y);
    }

    private static byte[] StructToBytes<T>(T value, int size) where T : struct
    {
        var bytes = new byte[size];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
            return bytes;
        }
        finally
        {
            handle.Free();
        }
    }

    private static T BytesToStruct<T>(byte[] bytes) where T : struct
    {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }
}
