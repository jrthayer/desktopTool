using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopTool.Features.Fences;
using DesktopTool.UI;

namespace DesktopTool.Features.JunkDrawer;

/// <summary>The Junk Drawer's "Hide with Double Click": while enabled, double-clicking an empty part
/// of the desktop toggles Hide Desktop (see DesktopHider). Desktop clicks go to Explorer, not this
/// app, so this watches them with a low-level mouse hook - installed only while the setting is on.
///
/// The hook callback itself only notes left-button presses and spots a double-click by time and
/// distance (Windows drops a hook that's slow to return). The heavier check - is that spot really
/// empty desktop, with no fence or icon there (FenceManager.IsEmptyDesktopAt) - runs afterwards on
/// the UI thread. Clicks still reach Explorer as usual; nothing is swallowed.</summary>
internal sealed class DesktopDoubleClickWatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    private readonly FenceManager _fences;
    private readonly SynchronizationContext _uiContext;
    // Kept in a field so the delegate handed to Windows is never garbage-collected while hooked.
    private readonly LowLevelMouseProc _callback;

    private IntPtr _hook;
    private long _lastClickTicks;
    private Point _lastClickPoint;

    public DesktopDoubleClickWatcher(FenceManager fences)
    {
        _fences = fences;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _callback = HookCallback;
    }

    public bool IsEnabled => _hook != IntPtr.Zero;

    public void SetEnabled(bool enabled)
    {
        if (enabled == IsEnabled)
            return;

        if (enabled)
        {
            using var module = Process.GetCurrentProcess().MainModule;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _callback, GetModuleHandle(module?.ModuleName), 0);
        }
        else
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var point = new Point(info.X, info.Y);
            var now = Stopwatch.GetTimestamp();

            var elapsedMs = (now - _lastClickTicks) * 1000 / Stopwatch.Frequency;
            var size = SystemInformation.DoubleClickSize;
            var isDoubleClick = elapsedMs <= SystemInformation.DoubleClickTime
                && Math.Abs(point.X - _lastClickPoint.X) <= size.Width / 2
                && Math.Abs(point.Y - _lastClickPoint.Y) <= size.Height / 2;

            if (isDoubleClick)
            {
                // A third click shouldn't count as a second double-click.
                _lastClickTicks = 0;
                _uiContext.Post(_ => OnDoubleClick(point), null);
            }
            else
            {
                _lastClickTicks = now;
                _lastClickPoint = point;
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void OnDoubleClick(Point point)
    {
        if (IsEnabled && _fences.IsEmptyDesktopAt(point))
            DesktopHider.Toggle();
    }

    public void Dispose() => SetEnabled(false);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
