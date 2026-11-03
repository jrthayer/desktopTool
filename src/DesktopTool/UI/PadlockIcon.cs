using System.Drawing.Drawing2D;

namespace DesktopTool.UI;

/// <summary>Hand-drawn padlock - closed for a locked fence, with the shackle swung open for one that's
/// open right now (unlocked this session, or inside its time window). Same "just draw the shape"
/// approach as InfoIcon/WarningIcon.</summary>
internal static class PadlockIcon
{
    public static void Paint(Graphics g, Rectangle rect, Color color, bool open)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bodyHeight = rect.Height * 0.55f;
        var bodyWidth = rect.Width * 0.78f;
        var body = new RectangleF(rect.X + (rect.Width - bodyWidth) / 2f, rect.Bottom - bodyHeight, bodyWidth, bodyHeight);

        var shackleWidth = bodyWidth * 0.62f;
        var shackleHeight = rect.Height * 0.62f;
        var shackleX = body.X + (bodyWidth - shackleWidth) / 2f;
        // Open: the shackle lifts and swings right, so its left leg leaves the body.
        if (open)
            shackleX += shackleWidth * 0.55f;
        var shackleTop = rect.Y + (open ? -rect.Height * 0.08f : 0f);
        var shackle = new RectangleF(shackleX, shackleTop, shackleWidth, shackleHeight);

        using (var pen = new Pen(color, Math.Max(1.4f, rect.Width / 9f)))
        {
            using var path = new GraphicsPath();
            path.AddArc(shackle.X, shackle.Y, shackle.Width, shackle.Width, 180, 180);
            path.AddLine(shackle.Right, shackle.Y + shackle.Width / 2f, shackle.Right, body.Y + 1);
            path.StartFigure();
            path.AddLine(shackle.X, shackle.Y + shackle.Width / 2f, shackle.X, open ? shackle.Y + shackle.Width * 0.9f : body.Y + 1);
            g.DrawPath(pen, path);
        }

        using (var brush = new SolidBrush(color))
            g.FillRectangle(brush, body.X, body.Y, body.Width, body.Height);

        g.SmoothingMode = previous;
    }
}
