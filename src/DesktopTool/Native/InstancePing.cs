using System.Runtime.InteropServices;

namespace DesktopTool.Native;

/// <summary>
/// Lets a second launch tell a healthy running instance from a stuck one. The running instance owns
/// an invisible top-level window with a fixed title that answers a registered "ping" message; a
/// second launch finds it (FindWindow only sees the current session's desktop, so another user's or
/// another session's copy never answers) and sends the ping with a timeout. An answer means the
/// running instance's UI thread is alive, so it just surfaces itself; no answer means it's hung (or
/// never got as far as its message loop) and is safe to offer to restart.
/// </summary>
internal sealed class InstancePing : NativeWindow, IDisposable
{
    private const string WindowTitle = "DesktopTool.InstancePing";
    private static readonly uint PingMessage = NativeMethods.RegisterWindowMessage("DesktopTool-Ping");

    /// <summary>Raised on the UI thread when another launch pinged this instance.</summary>
    public event EventHandler? Pinged;

    public InstancePing()
    {
        CreateHandle(new CreateParams { Caption = WindowTitle });
        // A non-elevated second launch must still be able to ping an elevated first instance - UIPI
        // otherwise drops the message and the healthy instance would look hung.
        if (PingMessage != 0)
            ChangeWindowMessageFilterEx(Handle, PingMessage, MSGFLT_ALLOW, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (PingMessage != 0 && m.Msg == (int)PingMessage)
        {
            m.Result = (IntPtr)1;
            Pinged?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();

    /// <summary>True if a running instance in this session answered within timeoutMs. PostMessage-style
    /// delivery isn't enough here - only a SendMessageTimeout reply proves its message loop is running.</summary>
    public static bool PingExisting(uint timeoutMs)
    {
        if (PingMessage == 0)
            return false;

        var hWnd = NativeMethods.FindWindow(null, WindowTitle);
        if (hWnd == IntPtr.Zero)
            return false;

        return NativeMethods.SendMessageTimeout(hWnd, PingMessage, IntPtr.Zero, IntPtr.Zero,
                   NativeMethods.SMTO_ABORTIFHUNG, timeoutMs, out var result) != IntPtr.Zero
               && result == (IntPtr)1;
    }

    private const uint MSGFLT_ALLOW = 1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr pChangeFilterStruct);
}
