using DesktopTool.Native;

namespace DesktopTool.UI;

/// <summary>"Hide Desktop" / "Show Desktop": hides every DesktopTool widget on screen at once - fences,
/// folder fences, Layout Launcher, Wallpapers, Widget Manager, the Junk Drawer itself, anything on
/// LayeredWidgetForm - and brings back exactly those later. Widgets that were already hidden stay
/// hidden when the desktop is shown again.
///
/// Uses raw ShowWindow (the same call FenceForm.SetVisible makes) rather than each widget's own
/// hide/show, so no widget's saved "visible" setting changes - and the hidden state itself isn't
/// saved either, so a restart always comes back with everything showing.</summary>
internal static class DesktopHider
{
    private static readonly List<LayeredWidgetForm> _hidden = new();

    public static bool IsHidden { get; private set; }

    /// <summary>Raised after Hide or Show actually changes IsHidden.</summary>
    public static event EventHandler? Changed;

    public static void Hide()
    {
        if (IsHidden)
            return;

        _hidden.Clear();
        foreach (var widget in LayeredWidgetForm.LiveWidgets.ToList())
        {
            if (widget.IsDisposed || !widget.IsHandleCreated || !NativeMethods.IsWindowVisible(widget.Handle))
                continue;
            _hidden.Add(widget);
            NativeMethods.ShowWindow(widget.Handle, NativeMethods.SW_HIDE);
        }

        IsHidden = true;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Show()
    {
        if (!IsHidden)
            return;

        foreach (var widget in _hidden)
        {
            if (!widget.IsDisposed && widget.IsHandleCreated)
                NativeMethods.ShowWindow(widget.Handle, NativeMethods.SW_SHOWNOACTIVATE);
        }
        _hidden.Clear();

        IsHidden = false;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Toggle()
    {
        if (IsHidden)
            Show();
        else
            Hide();
    }
}
