using System.Drawing.Text;

namespace DesktopTool.UI;

/// <summary>The app's hand-drawn on/off switch pill, used by every widget with switch rows (Widget
/// Manager, Junk Drawer). On is a solid white pill with "On" punched out in the row's own background
/// color (rowBackground), so the label reads as a cutout; Off is just an outline in offBorderColor
/// with plain WhiteSmoke text, since it has no fill for a cutout to contrast against.</summary>
internal static class ToggleSwitch
{
    public const int Width = 38;
    public const int Height = 18;

    // Smaller than the widgets' own 9pt font - "On"/"Off" at 9pt doesn't fit an 18px pill. Created
    // once rather than per paint.
    private static readonly Font LabelFont = new(AppTheme.Font.FontFamily, 7f);

    public static void Paint(Graphics g, Rectangle rect, bool on, Color rowBackground, Color offBorderColor)
    {
        using var path = RoundedRectPath.Full(rect, rect.Height / 2);
        if (on)
        {
            using var fill = new SolidBrush(Color.White);
            g.FillPath(fill, path);
        }
        else
        {
            using var border = new Pen(offBorderColor);
            g.DrawPath(border, path);
        }

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(on ? rowBackground : Color.WhiteSmoke))
        using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(on ? "On" : "Off", LabelFont, textBrush, rect, textFormat);
        g.TextRenderingHint = previousTextHint;
    }
}
