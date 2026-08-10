using System.Drawing.Drawing2D;

namespace DesktopTool.UI;

/// <summary>Rounded-corner GDI+ paths for a hand-painted layered window's own body/title fills
/// (FenceForm, LayoutLauncherWidget) - lifted out of FenceForm's own private RoundedRect/
/// RoundedRectTop, which both classes now share instead of each keeping their own copy.</summary>
internal static class RoundedRectPath
{
    public static GraphicsPath Full(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        // AddArc throws for a zero (or negative) width/height bounding box - a plain square-cornered
        // rectangle instead, now that Corner Radius (see IWidgetStyle) is a user-adjustable setting
        // whose floor is legitimately 0, not just a fixed positive constant like this used to always
        // be called with.
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Rounded on the top two corners only, square across the bottom - for a title/header
    /// band that sits flush against the rest of a rounded body beneath it.</summary>
    public static GraphicsPath Top(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
        path.CloseFigure();
        return path;
    }

    /// <summary>The mirror of Top, for stroking rather than filling: rounded on the bottom two
    /// corners only, with NO top edge at all (not closed back across the top the way Full/Top both
    /// are) - its left/right edges start at topInset rather than bounds.Y. For a widget's own body
    /// border that needs to stop short of a separately-bordered header instead of implicitly
    /// wrapping it too - see LayeredWidgetForm.PaintChrome's own body-border stroke.</summary>
    public static GraphicsPath Bottom(Rectangle bounds, int radius, int topInset)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddLine(bounds.X, bounds.Y + topInset, bounds.X, bounds.Bottom);
            path.AddLine(bounds.X, bounds.Bottom, bounds.Right, bounds.Bottom);
            path.AddLine(bounds.Right, bounds.Bottom, bounds.Right, bounds.Y + topInset);
            return path;
        }
        int d = radius * 2;
        path.AddLine(bounds.X, bounds.Y + topInset, bounds.X, bounds.Bottom - radius);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 180, -90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 90, -90);
        path.AddLine(bounds.Right, bounds.Bottom - radius, bounds.Right, bounds.Y + topInset);
        return path;
    }

    /// <summary>The fillable counterpart to Bottom above (which is deliberately left open across
    /// the top, for stroking) - same rounded-bottom-only shape, closed off with a flat top edge at
    /// topInset so it can be filled as its own region. Lets a widget's own body fill stop exactly
    /// where its header begins instead of first filling the whole rounded body (rounding top
    /// corners the header's own Top fill is about to paint over anyway) and then re-filling Top
    /// directly on top of that already-antialiased edge - two separately-antialiased fills sharing
    /// the same top-corner boundary otherwise leaves a faint seam along both the left and right
    /// rounded corners, where the first fill's own partial-coverage edge pixels show through the
    /// second - see LayeredWidgetForm.PaintChrome's own body/title fill order.</summary>
    public static GraphicsPath BottomFilled(Rectangle bounds, int radius, int topInset)
    {
        var path = Bottom(bounds, radius, topInset);
        // CloseFigure draws a straight line back to the figure's own start point (bounds.X,
        // bounds.Y + topInset) from wherever Bottom's own last segment left off (bounds.Right,
        // bounds.Y + topInset) - exactly the flat top edge this needs, with no separate AddLine call.
        path.CloseFigure();
        return path;
    }

    /// <summary>Same as Top, but with a square (unrounded) top-left corner instead of a rounded
    /// one - for a header whose own top-left corner sits directly below something else that
    /// defines the widget's real outline there instead (FolderFenceForm's own folder tab, whose
    /// combined border path runs a plain straight line down through x=0 at this exact height - see
    /// FolderFenceForm.GetBodyOutlinePath). The header's own fill has to actually reach that same
    /// x=0 column with no rounded cutout of its own, or the corner reads as a real bite taken out of
    /// the header - the tab sits entirely above this fill (never overlapping it downward into the
    /// header's own y-range), so nothing else is there to cover a rounded cutout if this rounds it.</summary>
    public static GraphicsPath TopSquareTopLeft(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        int d = radius * 2;
        path.AddLine(bounds.X, bounds.Y, bounds.Right - d, bounds.Y);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
        path.CloseFigure();
        return path;
    }
}
