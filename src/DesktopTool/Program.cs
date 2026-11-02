using System.Runtime.InteropServices;

namespace DesktopTool;

internal static class Program
{
    private const string MutexName = "Global\\DesktopTool-SingleInstance-9f2b8e3a";

    [STAThread]
    private static void Main()
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but was created by an instance with a different security
            // context (typically one running elevated), so we can't open it. It's still
            // another running instance.
            MessageBox.Show("Fence Tool is already running as Administrator (check the system tray).",
                "Fence Tool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var _ = mutex;
        if (!createdNew)
        {
            MessageBox.Show("Fence Tool is already running (check the system tray).", "Fence Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AllowTaskbarCreatedWhenElevated();

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
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
