using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DesktopTool.Features.Fences.Native;
using DesktopTool.Native;

namespace DesktopTool;

internal static class Program
{
    // Local\ (this logon session only) plus the user's SID, not Global\. A Global\ name is shared by
    // every session on the machine, so a copy running under another account - or in an earlier,
    // disconnected session of this one that Windows kept alive - held it and made every launch here
    // report "already running" while nothing showed up, until a sign-out tore that session down.
    private static readonly string MutexName =
        $"Local\\DesktopTool-SingleInstance-{WindowsIdentity.GetCurrent().User?.Value ?? "default"}";

    // How long a second launch waits for the running instance to answer before calling it stuck.
    // Generous, since a healthy instance can still be busy building its fences at startup.
    private const uint PingTimeoutMs = 5000;

    // How long to wait at logon for Explorer's taskbar and desktop to exist before starting anyway.
    private static readonly TimeSpan ShellWaitLimit = TimeSpan.FromSeconds(60);

    [STAThread]
    private static void Main()
    {
        // Before any window (the ping window included) is created - it sets the DPI mode.
        ApplicationConfiguration.Initialize();

        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but an instance at a different integrity level (typically elevated)
            // made it, so it can't be opened - or waited on, so it can't be taken over either.
            if (!InstancePing.PingExisting(PingTimeoutMs))
                MessageBox.Show("Fence Tool is already running as Administrator but isn't responding.\n\n" +
                                "End DesktopTool.exe in Task Manager, then start Fence Tool again.",
                    "Fence Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var _ = mutex;
        if (!createdNew && !TakeOverFromExistingInstance(mutex))
            return;

        using var ping = new InstancePing();

        AllowTaskbarCreatedWhenElevated();
        WaitForShell();

        var context = new TrayApplicationContext();
        ping.Pinged += (_, _) => context.SurfaceForRelaunch();
        Application.Run(context);
    }

    /// <summary>Another instance holds the mutex. If it answers a ping it's healthy and has just been
    /// told to surface itself, so this launch quits. If it doesn't, it's stuck (typically frozen at
    /// logon before its tray icon ever appeared) - offer to end it and start fresh instead of making
    /// the user kill it and sign out. True means this launch now owns the mutex and should start.</summary>
    private static bool TakeOverFromExistingInstance(Mutex mutex)
    {
        if (InstancePing.PingExisting(PingTimeoutMs))
            return false;

        var answer = MessageBox.Show(
            "Fence Tool is already running but isn't responding (it may have got stuck while Windows was starting).\n\n" +
            "End it and start Fence Tool again?",
            "Fence Tool", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return false;

        var self = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(self.ProcessName))
        {
            using (other)
            {
                if (other.Id == self.Id || other.SessionId != self.SessionId)
                    continue;
                try
                {
                    other.Kill();
                    other.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Already gone, or not ours to end (elevated) - the mutex wait below decides.
                }
            }
        }

        try
        {
            if (mutex.WaitOne(TimeSpan.FromSeconds(10)))
                return true;
        }
        catch (AbandonedMutexException)
        {
            // Expected: the killed instance never released it. Ownership has passed to this one.
            return true;
        }

        MessageBox.Show("Couldn't end the stuck copy of Fence Tool.\n\nEnd DesktopTool.exe in Task Manager, then start Fence Tool again.",
            "Fence Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    /// <summary>At logon this process can start before Explorer has built its taskbar and desktop.
    /// Fences talk to the desktop's icon list as they load, and the tray icon needs the taskbar, so
    /// wait (pumping messages, so a second launch's ping still gets answered) until both exist. Gives
    /// up after ShellWaitLimit and starts anyway - the TaskbarCreated and rediscovery paths already
    /// cope with an Explorer that turns up late.</summary>
    private static void WaitForShell()
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < ShellWaitLimit)
        {
            if (NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero
                && DesktopListView.FindDesktopDefView().DefView != IntPtr.Zero)
                return;

            Application.DoEvents();
            Thread.Sleep(500);
        }
    }

    // When this process runs elevated, UIPI drops the "TaskbarCreated" broadcast that a
    // (non-elevated) Explorer sends after it restarts. NotifyIcon relies on that message to
    // re-add the tray icon, so without this the icon silently disappears for good after any
    // Explorer restart. The process-wide filter also covers BackgroundMessageWindow, which
    // listens for the same message. Harmless when not elevated.
    private static void AllowTaskbarCreatedWhenElevated()
    {
        var msg = RegisterWindowMessage("TaskbarCreated");
        if (msg != 0)
            ChangeWindowMessageFilter(msg, MSGFLT_ADD);
    }

    private const uint MSGFLT_ADD = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);
}
