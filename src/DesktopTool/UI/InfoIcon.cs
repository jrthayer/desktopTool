using System.Drawing.Drawing2D;

namespace DesktopTool.UI;

/// <summary>Hand-drawn circled "i" info icon, shared by Claude Toolbox's row info icon and Widget
/// Manager's header help button - same "no icon asset library, just draw the shape" approach as
/// WarningIcon (see its own class comment).</summary>
internal static class InfoIcon
{
    /// <summary>Colored to match the caller's own surrounding text (not a fixed accent) so it reads
    /// as part of whatever it's sitting on instead of a differently-styled sticker.</summary>
    public static void Paint(Graphics g, Rectangle rect, Color color)
    {
        var previousSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var pen = new Pen(color, 1.2f))
            g.DrawEllipse(pen, rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1f, rect.Height - 1f);

        var cx = rect.X + rect.Width / 2f;
        using (var brush = new SolidBrush(color))
        {
            var dotSize = rect.Width * 0.16f;
            g.FillEllipse(brush, cx - dotSize / 2f, rect.Y + rect.Height * 0.24f, dotSize, dotSize);

            var barWidth = rect.Width * 0.16f;
            var barTop = rect.Y + rect.Height * 0.46f;
            var barHeight = rect.Height * 0.32f;
            g.FillRectangle(brush, cx - barWidth / 2f, barTop, barWidth, barHeight);
        }

        g.SmoothingMode = previousSmoothing;
    }
}
