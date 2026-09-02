using System.Runtime.InteropServices;

namespace DesktopTool.Features.Fences.Native;

/// <summary>
/// Raises <see cref="Changed"/> whenever the Windows Recycle Bin's contents change - something
/// deleted into it, restored out of it, or the whole thing emptied - regardless of who did it:
/// this app's own drop-to-trash, Explorer, another tool, or the "Empty Recycle Bin" command.
/// Backs the fence trash item flipping between its empty and full icon (see
/// FenceForm.RefreshRecycleBinIcon) - without this, the fence would only ever re-extract that
/// icon on the drops it performs itself and would silently miss every external change.
///
/// Uses SHChangeNotifyRegister scoped to the Recycle Bin's own shell PIDL (recursive, so a child
/// item add/remove counts) rather than a FileSystemWatcher on C:\$Recycle.Bin - that path is
/// ACL-locked per user and its on-disk layout is undocumented, whereas this shell notification is
/// exactly the mechanism Explorer's own desktop icon restyles itself from. A plain hidden
/// NativeWindow (never shown, so invisible) is enough: SHChangeNotifyRegister PostMessages the one
/// hwnd it's given, it isn't a broadcast - contrast BackgroundMessageWindow, which must be a real
/// top-level window precisely because "TaskbarCreated" is a broadcast.
/// </summary>
internal sealed class RecycleBinChangeWatcher : NativeWindow, IDisposable
{
    private const int CSIDL_BITBUCKET = 0x000a; // the Recycle Bin
    private const int WM_RECYCLEBIN_NOTIFY = 0x0401; // WM_USER + 1 - private to this window's queue

    // SHChangeNotifyRegister fSources flags.
    private const int SHCNRF_InterruptLevel = 0x0001;
    private const int SHCNRF_ShellLevel = 0x0002;
    private const int SHCNRF_RecursiveInterrupt = 0x1000; // must accompany InterruptLevel + fRecursive
    private const int SHCNRF_NewDelivery = 0x8000; // payload via shared memory - Lock/Unlock to read

    private const int SHCNE_ALLEVENTS = 0x7FFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHChangeNotifyEntry
    {
        public IntPtr pidl;
        [MarshalAs(UnmanagedType.Bool)] public bool fRecursive;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetSpecialFolderLocation(IntPtr hwndOwner, int nFolder, out IntPtr ppidl);

    [DllImport("shell32.dll")]
    private static extern uint SHChangeNotifyRegister(IntPtr hwnd, int fSources, int fEvents, uint wMsg,
        int cEntries, ref SHChangeNotifyEntry pshcne);

    [DllImport("shell32.dll")]
    private static extern bool SHChangeNotifyDeregister(uint ulID);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHChangeNotification_Lock(IntPtr hChange, uint dwProcId, out IntPtr ppidl, out int plEvent);

    [DllImport("shell32.dll")]
    private static extern bool SHChangeNotification_Unlock(IntPtr hLock);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private uint _registrationId;
    private IntPtr _binPidl;

    /// <summary>Fires on the thread that constructed this watcher (the UI thread - its window is
    /// created there, so its messages pump on the UI message loop). The shell coalesces bursts, so
    /// a batch of deletes may arrive as a single call.</summary>
    public event Action? Changed;

    public RecycleBinChangeWatcher()
    {
        // All-default CreateParams: no WS_VISIBLE, no parent - an ordinary top-level window that's
        // simply never shown. CreateHandle only needs the HWND to exist; messages queue until the
        // message loop runs.
        CreateHandle(new CreateParams());

        if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_BITBUCKET, out _binPidl) != 0 || _binPidl == IntPtr.Zero)
            return;

        var entry = new SHChangeNotifyEntry { pidl = _binPidl, fRecursive = true };
        _registrationId = SHChangeNotifyRegister(
            Handle,
            SHCNRF_ShellLevel | SHCNRF_InterruptLevel | SHCNRF_RecursiveInterrupt | SHCNRF_NewDelivery,
            SHCNE_ALLEVENTS,
            WM_RECYCLEBIN_NOTIFY,
            1,
            ref entry);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_RECYCLEBIN_NOTIFY)
        {
            // SHCNRF_NewDelivery: the affected-PIDL/event payload lives in shared memory that has
            // to be locked to touch and unlocked to release - even though none of that detail is
            // needed here (the registration is already scoped to the bin), skipping the unlock
            // leaks the block. A null lock handle just means there was nothing to read.
            var hLock = SHChangeNotification_Lock(m.WParam, (uint)m.LParam.ToInt64(), out _, out _);
            if (hLock != IntPtr.Zero)
            {
                SHChangeNotification_Unlock(hLock);
                Changed?.Invoke();
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registrationId != 0)
        {
            SHChangeNotifyDeregister(_registrationId);
            _registrationId = 0;
        }

        if (_binPidl != IntPtr.Zero)
        {
            CoTaskMemFree(_binPidl);
            _binPidl = IntPtr.Zero;
        }

        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }
}
