using System.Runtime.InteropServices;

namespace DesktopTool.Features.GetShitDone;

/// <summary>
/// Objects to a shutdown/restart/sign-out while a requirement isn't met, so Windows shows its own
/// "this app is preventing shutdown" screen with the reason on it. Only ever a delay: that screen's
/// "Shut down anyway" still works, a forced shutdown isn't objected to at all, and sleep or closing
/// the lid never asks. Its own hidden top-level window rather than a hook on one of the widgets -
/// WM_QUERYENDSESSION goes to every top-level window, shown or not, and a widget can be hidden.
/// Has to be created on the UI thread. Not constructed anywhere yet.
/// </summary>
internal sealed class ShutdownGate : NativeWindow, IDisposable
{
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;
    private const long ENDSESSION_CRITICAL = 0x40000000;

    // Without a registered reason, an app with no visible window that refuses WM_QUERYENDSESSION is
    // simply ended rather than shown on the blocking screen.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    private readonly Func<string?> _unmet;

    /// <summary>What's still missing, each time a shutdown is objected to - the moment to bring the
    /// widget forward so it can be dealt with.</summary>
    public event Action<string>? Blocked;

    /// <summary>unmet returns null once shutting down is allowed - see GateRule.Unmet.</summary>
    public ShutdownGate(Func<string?> unmet)
    {
        _unmet = unmet;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_QUERYENDSESSION when (m.LParam.ToInt64() & ENDSESSION_CRITICAL) == 0 && _unmet() is { } reason:
                ShutdownBlockReasonCreate(Handle, reason);
                m.Result = IntPtr.Zero;
                Blocked?.Invoke(reason);
                return;

            // Sent either way once every window has answered - wParam 0 means the shutdown was
            // called off, so the reason shouldn't linger into the next attempt.
            case WM_ENDSESSION when m.WParam == IntPtr.Zero:
                ShutdownBlockReasonDestroy(Handle);
                break;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero)
            return;
        ShutdownBlockReasonDestroy(Handle);
        DestroyHandle();
    }
}
