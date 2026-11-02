using DesktopTool.Native;
using DesktopTool.Features.Snapping;

namespace DesktopTool.Features.Fences.UI;

/// <summary>
/// Non-interactive, click-through display of snap guide lines - shown at idle (custom lines only,
/// while the "Show Snap Lines" tray toggle is on) and during an active fence drag (custom lines plus
/// whichever other-fence edges are currently snapped to, drawn highlighted). Modeled on
/// EyedropperOverlay's virtual-screen-spanning colorkey approach, but click-through
/// (WS_EX_TRANSPARENT) and non-activating (WS_EX_NOACTIVATE, shown via FenceForm's own raw
/// ShowWindow/SW_SHOWNOACTIVATE pattern rather than WinForms' Show()) since this must never
/// interrupt the OS's own native fence-drag loop or steal focus while sitting on screen at idle.
/// </summary>
internal sealed class SnapGuideOverlay : Form
{
    private static readonly Color KeyColor = Color.FromArgb(255, 1, 2, 3);
    private static readonly Color LineColor = Color.FromArgb(160, 255, 255, 255);
    private static readonly Color HighlightColor = Color.FromArgb(220, 255, 90, 90);

    private IReadOnlyList<(SnapOrientation Orientation, int Position, bool Highlighted, Rectangle Span)> _lines =
        Array.Empty<(SnapOrientation, int, bool, Rectangle)>();

    public SnapGuideOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
    }

    public new void Show() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);

    public new void Hide() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);

    /// <summary>Re-spans whatever the current monitor layout is - the same WM_DISPLAYCHANGE/
    /// WM_DPICHANGED trigger FenceForm.Reanchor responds to.</summary>
    public void UpdateVirtualScreenBounds() => Bounds = SystemInformation.VirtualScreen;

    /// <summary>Replaces the full set of lines currently drawn. Called at mouse-move rate during a
    /// live drag (WM_MOVING/WM_SIZING fire that often), so this forces a synchronous repaint via
    /// Update() rather than just Invalidate() - otherwise the guide would visibly lag the cursor,
    /// trailing behind whatever else is pumping this thread's message loop mid-drag.</summary>
    public void SetLines(IReadOnlyList<(SnapOrientation Orientation, int Position, bool Highlighted, Rectangle Span)> lines)
    {
        _lines = lines;
        Invalidate();
        Update();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        // span is the line's monitor working area. A line at its exclusive Right/Bottom edge (the
        // default line along the taskbar, say) would otherwise paint its first pixel column/row
        // INSIDE the taskbar, so the drawn position is pulled into the last pixel still within span
        // - the snap value itself is untouched.
        foreach (var (orientation, position, highlighted, span) in _lines)
        {
            using var pen = new Pen(highlighted ? HighlightColor : LineColor, highlighted ? 2f : 1f);
            if (orientation == SnapOrientation.Horizontal)
            {
                var y = PointToClient(new Point(0, Math.Clamp(position, span.Top, span.Bottom - 1))).Y;
                var left = PointToClient(new Point(span.Left, 0)).X;
                var right = PointToClient(new Point(span.Right - 1, 0)).X;
                e.Graphics.DrawLine(pen, left, y, right, y);
            }
            else
            {
                var x = PointToClient(new Point(Math.Clamp(position, span.Left, span.Right - 1), 0)).X;
                var top = PointToClient(new Point(0, span.Top)).Y;
                var bottom = PointToClient(new Point(0, span.Bottom - 1)).Y;
                e.Graphics.DrawLine(pen, x, top, x, bottom);
            }
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 // WS_EX_TOOLWINDOW - keep it out of the taskbar/alt-tab
                | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }
}
