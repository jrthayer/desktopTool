namespace DesktopTool.Features.Wallpapers;

/// <summary>Pure geometry for the Wallpapers widget's monitor map: scales the real monitor
/// arrangement down into a target area, keeping relative positions and aspect ratios.</summary>
public static class MonitorMapLayout
{
    /// <summary>Each monitor's rect scaled uniformly into area and centered there, in the same order
    /// as monitors. Each tile is then inset by gap/2 on every side so neighbouring monitors (which
    /// share an edge in screen space) read as separate tiles. Empty input gives empty output.</summary>
    public static IReadOnlyList<Rectangle> Fit(IReadOnlyList<Rectangle> monitors, Rectangle area, int gap)
    {
        if (monitors.Count == 0 || area.Width <= 0 || area.Height <= 0)
            return Array.Empty<Rectangle>();

        var union = monitors[0];
        foreach (var monitor in monitors.Skip(1))
            union = Rectangle.Union(union, monitor);

        if (union.Width <= 0 || union.Height <= 0)
            return Array.Empty<Rectangle>();

        var scale = Math.Min((double)area.Width / union.Width, (double)area.Height / union.Height);
        var offsetX = area.X + (area.Width - union.Width * scale) / 2;
        var offsetY = area.Y + (area.Height - union.Height * scale) / 2;
        var inset = gap / 2.0;

        var result = new List<Rectangle>(monitors.Count);
        foreach (var monitor in monitors)
        {
            var left = offsetX + (monitor.Left - union.Left) * scale + inset;
            var top = offsetY + (monitor.Top - union.Top) * scale + inset;
            var right = offsetX + (monitor.Right - union.Left) * scale - inset;
            var bottom = offsetY + (monitor.Bottom - union.Top) * scale - inset;
            var l = Round(left);
            var t = Round(top);
            result.Add(Rectangle.FromLTRB(l, t, Math.Max(l + 1, Round(right)), Math.Max(t + 1, Round(bottom))));
        }
        return result;
    }

    // Always rounds .5 the same direction - the default banker's rounding would round a tile's two
    // edges in opposite directions and shave a pixel off its size.
    private static int Round(double value) => (int)Math.Floor(value + 0.5);
}
