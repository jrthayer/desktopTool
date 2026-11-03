using DesktopTool.UI;

namespace DesktopTool.Features.FenceLocker.UI;

/// <summary>The padlock both fence types draw in their header: closed (bright) while locked, open
/// (dimmed) while the lock is open right now, nothing when there's no lock.</summary>
internal static class FenceLockBadge
{
    public const int Size = 13;

    /// <summary>Content-space rect at the header's right end, left of the header close button when
    /// one is shown (closeButtonSize 0 when it isn't). A fence with its header hidden passes
    /// titleVisible false and gets the body's own top-right corner instead, so the padlock still shows.</summary>
    public static Rectangle Rect(int contentWidth, int titleRowHeight, int closeButtonSize, bool titleVisible)
    {
        if (!titleVisible)
            return new Rectangle(contentWidth - 6 - Size, 5, Size, Size);
        return Rect(contentWidth, titleRowHeight, closeButtonSize);
    }

    public static Rectangle Rect(int contentWidth, int titleRowHeight, int closeButtonSize)
    {
        var right = contentWidth - 8 - (closeButtonSize > 0 ? closeButtonSize + 6 : 0);
        return new Rectangle(right - Size, (titleRowHeight - Size) / 2, Size, Size);
    }

    /// <summary>The padlock's clickable area - a little bigger than the glyph so it's easy to hit.</summary>
    public static Rectangle HitRect(Rectangle badgeRect) => Rectangle.Inflate(badgeRect, 4, 4);

    /// <summary>What clicking a fence's padlock does: the same menu as that fence's row in Manage
    /// Locks (Unlock... or Lock, Change lock..., Remove lock...), opened just under the padlock -
    /// the way in for a fence with nothing in it to click.</summary>
    public static void HandleClick(Guid fenceId, string fenceName, Control owner, Rectangle badgeScreenRect) =>
        FenceLockMenu.Show(fenceId, fenceName, owner, new Point(badgeScreenRect.Left, badgeScreenRect.Bottom + 2), includeHistory: false);

    public static void Paint(Graphics g, Rectangle windowRect, FenceLockStatus status)
    {
        switch (status)
        {
            case FenceLockStatus.Locked:
                PadlockIcon.Paint(g, windowRect, Color.WhiteSmoke, open: false);
                break;
            case FenceLockStatus.OpenByTime:
            case FenceLockStatus.UnlockedThisSession:
                PadlockIcon.Paint(g, windowRect, AppTheme.DisabledText, open: true);
                break;
        }
    }
}
