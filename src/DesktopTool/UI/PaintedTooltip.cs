using System.Drawing.Text;

namespace DesktopTool.UI;

/// <summary>A small hand-painted tooltip pill (dark rounded-rect + centered text) - a standalone,
/// dependency-free utility rather than something tied to LayeredWidgetForm, so any hand-painted UI in
/// this app can use it, not just a LayeredWidgetForm subclass. Anchored to a caller-supplied target
/// rect and clamped to a caller-supplied bounds rect, since (unlike a real floating OS tooltip, and
/// unlike System.Windows.Forms.ToolTip - see LayoutLauncherWidget's own history with that control's
/// fade-in flash) this is painted directly into whatever bitmap/Graphics the caller already owns, not
/// a separate popup window - it can never extend past the bounds it's given. Every coordinate in and
/// out is in whatever space the caller's own Graphics draws in - this class does no space conversion
/// of its own (no ToWindow, no content-vs-window distinction), so it works identically whether the
/// caller is a LayeredWidgetForm subclass or something else entirely.</summary>
internal sealed class PaintedTooltip
{
    private const int PaddingX = 8;
    private const int PaddingY = 4;
    private const int MinHeight = 22;
    private const int Gap = 4;

    private string? _text;
    private Rectangle _targetRect;

    public bool IsVisible => _text is not null;

    /// <summary>Sets the currently-shown text/target - returns true if this actually changed
    /// anything, so the caller knows whether a repaint is needed. Re-showing the same text at a new
    /// target still counts as a change (the pill needs to move).</summary>
    public bool Show(string text, Rectangle targetRect)
    {
        if (text == _text && targetRect == _targetRect)
            return false;
        _text = text;
        _targetRect = targetRect;
        return true;
    }

    /// <summary>Hides it - returns true if it was actually showing, so the caller knows whether a
    /// repaint is needed.</summary>
    public bool Hide()
    {
        if (_text is null)
            return false;
        _text = null;
        return true;
    }

    /// <summary>Where the pill would paint, given the same font/bounds Paint itself would use - null
    /// while not visible. Pulled out of Paint so a caller can also use this alone, without actually
    /// painting - a LayeredWidgetForm's own GetFullOpacityRegions calls this to know which rect needs
    /// exempting from Style.Opacity's own fade, since this tooltip belongs to a bar button that's
    /// already exempted the same way and shouldn't visibly wash out while that button doesn't.</summary>
    public Rectangle? GetPillRect(Font font, Rectangle bounds)
    {
        if (_text is not { } text)
            return null;

        var maxTextWidth = Math.Max(1, bounds.Width - PaddingX * 2);
        var singleLineWidth = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

        int width, height;
        if (singleLineWidth <= maxTextWidth)
        {
            width = singleLineWidth + PaddingX * 2;
            height = MinHeight;
        }
        else
        {
            // Too wide for one line at this bounds - wrap within the available width and grow the
            // pill's height to fit instead of overflowing past bounds (see this class's own comment:
            // it can never extend past the bounds it's given). Only reached by callers feeding this
            // longer text than the short row-action strings it was originally sized for (e.g. a
            // feature's Description) - a short tooltip that already fits on one line takes the branch
            // above unchanged.
            var wrapped = TextRenderer.MeasureText(text, font, new Size(maxTextWidth, int.MaxValue), TextFormatFlags.WordBreak);
            width = wrapped.Width + PaddingX * 2;
            height = Math.Max(MinHeight, wrapped.Height + PaddingY * 2);
        }

        var x = Math.Clamp(_targetRect.X, bounds.X, Math.Max(bounds.X, bounds.Right - width));
        var below = _targetRect.Bottom + Gap;
        var y = below + height <= bounds.Bottom ? below : _targetRect.Top - Gap - height;
        y = Math.Clamp(y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - height));

        return new Rectangle(x, y, width, height);
    }

    /// <summary>Paints the pill, anchored just below targetRect by default and flipped to just above
    /// it when that would run past bounds.Bottom - bounds is whatever area the caller's own content
    /// is limited to (a widget's own content size, say). A no-op while not visible. borderColor is
    /// null by default, meaning no border is drawn at all - a caller with its own notion of when a
    /// border should show (a LayeredWidgetForm's own Header Border Mode, say) passes one only when it
    /// actually wants one right now; this class has no concept of that itself, by design (see its own
    /// class comment on staying dependency-free).</summary>
    public void Paint(Graphics g, Font font, Color background, Rectangle bounds, Color? borderColor = null)
    {
        if (_text is not { } text || GetPillRect(font, bounds) is not { } pillRect)
            return;

        using (var pillPath = RoundedRectPath.Full(pillRect, 6))
        using (var pillFill = new SolidBrush(background))
        {
            g.FillPath(pillFill, pillPath);
            if (borderColor is { } color)
            {
                using var borderPen = new Pen(color, 1f);
                g.DrawPath(borderPen, pillPath);
            }
        }

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(Color.WhiteSmoke))
        using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(text, font, textBrush, pillRect, textFormat);
        g.TextRenderingHint = previousTextHint;
    }
}
