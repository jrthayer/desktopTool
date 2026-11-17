using System.Drawing;
using DesktopTool.Features.WindowSnapping.Native;
using Xunit;

namespace DesktopTool.Tests;

public sealed class WindowSnapperTests
{
    private static Rectangle R(int l, int t, int r, int b) => Rectangle.FromLTRB(l, t, r, b);

    [Fact]
    public void SideBySideRightEdgeDragMovesNeighboursLeftEdge()
    {
        var left = R(0, 0, 960, 1040);
        var right = R(960, 0, 1920, 1040);

        var result = WindowSnapper.ComputeFollowerRects(left, R(0, 0, 1200, 1040), new[] { right });

        Assert.Equal(R(1200, 0, 1920, 1040), result[0]);
    }

    [Fact]
    public void SideBySideLeftEdgeDragMovesNeighboursRightEdge()
    {
        var left = R(0, 0, 960, 1040);
        var right = R(960, 0, 1920, 1040);

        var result = WindowSnapper.ComputeFollowerRects(right, R(700, 0, 1920, 1040), new[] { left });

        Assert.Equal(R(0, 0, 700, 1040), result[0]);
    }

    [Fact]
    public void QuadCentreLineMovesThroughAllFour()
    {
        var tl = R(0, 0, 960, 520);
        var tr = R(960, 0, 1920, 520);
        var bl = R(0, 520, 960, 1040);
        var br = R(960, 520, 1920, 1040);

        var result = WindowSnapper.ComputeFollowerRects(tl, R(0, 0, 1100, 520), new[] { tr, bl, br });

        Assert.Equal(R(1100, 0, 1920, 520), result[0]);
        Assert.Equal(R(0, 520, 1100, 1040), result[1]);
        Assert.Equal(R(1100, 520, 1920, 1040), result[2]);
    }

    [Fact]
    public void CornerDragMovesBothLines()
    {
        var tl = R(0, 0, 960, 520);
        var tr = R(960, 0, 1920, 520);
        var bl = R(0, 520, 960, 1040);
        var br = R(960, 520, 1920, 1040);

        var result = WindowSnapper.ComputeFollowerRects(tl, R(0, 0, 1000, 600), new[] { tr, bl, br });

        Assert.Equal(R(1000, 0, 1920, 600), result[0]);
        Assert.Equal(R(0, 600, 1000, 1040), result[1]);
        Assert.Equal(R(1000, 600, 1920, 1040), result[2]);
    }

    [Fact]
    public void SmallGapWithinToleranceStillLinks()
    {
        var left = R(0, 0, 953, 1040);
        var right = R(967, 0, 1920, 1040);

        var result = WindowSnapper.ComputeFollowerRects(left, R(0, 0, 1100, 1040), new[] { right });

        Assert.Equal(1100, result[0].Left);
    }

    [Fact]
    public void GapBeyondToleranceDoesNotLink()
    {
        var left = R(0, 0, 900, 1040);
        var right = R(960, 0, 1920, 1040);

        var result = WindowSnapper.ComputeFollowerRects(left, R(0, 0, 1100, 1040), new[] { right });

        Assert.Equal(right, result[0]);
    }

    [Fact]
    public void WindowOnSameLineButFarAwayAlongItDoesNotLink()
    {
        // Same x as the dragged edge, but on a monitor stacked well below - not touching.
        var left = R(0, 0, 960, 1040);
        var elsewhere = R(960, 3000, 1920, 4000);

        var result = WindowSnapper.ComputeFollowerRects(left, R(0, 0, 1100, 1040), new[] { elsewhere });

        Assert.Equal(elsewhere, result[0]);
    }

    [Fact]
    public void FollowerNeverShrinksBelowMinimum()
    {
        var left = R(0, 0, 960, 1040);
        var right = R(960, 0, 1920, 1040);

        var result = WindowSnapper.ComputeFollowerRects(left, R(0, 0, 1900, 1040), new[] { right });

        Assert.Equal(1920 - WindowSnapper.MinSize, result[0].Left);
        Assert.Equal(1920, result[0].Right);
    }

    [Fact]
    public void MoveIsDetectedAndResizeIsNot()
    {
        var start = R(0, 0, 960, 1040);

        Assert.True(WindowSnapper.IsMove(start, R(50, 30, 1010, 1070)));
        Assert.False(WindowSnapper.IsMove(start, R(0, 0, 1100, 1040)));
        Assert.False(WindowSnapper.IsMove(start, R(0, 100, 960, 1040)));
        Assert.False(WindowSnapper.IsMove(start, start));
    }

    [Fact]
    public void FollowRequestHoldsNewTargetsWhileOneIsInFlight()
    {
        var request = new FollowRequest(R(0, 0, 100, 100));

        Assert.True(request.Want(R(0, 0, 110, 100), now: 0));
        Assert.False(request.Want(R(0, 0, 120, 100), now: 5));
        Assert.False(request.Want(R(0, 0, 130, 100), now: 10));
        Assert.Equal(R(0, 0, 110, 100), request.Sent);
    }

    [Fact]
    public void FollowRequestSendsOnlyTheLatestTargetOnceAcknowledged()
    {
        var request = new FollowRequest(R(0, 0, 100, 100));
        request.Want(R(0, 0, 110, 100), now: 0);
        request.Want(R(0, 0, 120, 100), now: 5);
        request.Want(R(0, 0, 130, 100), now: 10);

        Assert.True(request.Acknowledge(now: 20));
        Assert.Equal(R(0, 0, 130, 100), request.Sent);
        Assert.False(request.Acknowledge(now: 30));
    }

    [Fact]
    public void FollowRequestResendsAfterTimeoutWithoutAcknowledgement()
    {
        var request = new FollowRequest(R(0, 0, 100, 100));
        request.Want(R(0, 0, 110, 100), now: 0);

        Assert.False(request.Want(R(0, 0, 120, 100), now: FollowRequest.AckTimeoutMs - 1));
        Assert.True(request.Want(R(0, 0, 130, 100), now: FollowRequest.AckTimeoutMs));
        Assert.Equal(R(0, 0, 130, 100), request.Sent);
    }

    [Fact]
    public void FollowRequestFlushSendsLatestEvenWhileInFlight()
    {
        var request = new FollowRequest(R(0, 0, 100, 100));
        request.Want(R(0, 0, 110, 100), now: 0);
        request.Want(R(0, 0, 150, 100), now: 5);

        Assert.True(request.Flush());
        Assert.Equal(R(0, 0, 150, 100), request.Sent);
        Assert.False(request.Flush());
    }

    [Fact]
    public void ClampTrimsOnlyTheEdgesOffScreen()
    {
        var workArea = R(0, 0, 1920, 1040);

        // Snapped flush to a neighbour's left edge at 960, but its right and bottom hang off screen.
        var result = WindowSnapper.ClampToWorkArea(R(960, 200, 2400, 1300), workArea);

        Assert.Equal(R(960, 200, 1920, 1040), result);
    }

    [Fact]
    public void ClampLeavesOnScreenWindowAlone()
    {
        var rect = R(100, 100, 900, 700);

        Assert.Equal(rect, WindowSnapper.ClampToWorkArea(rect, R(0, 0, 1920, 1040)));
    }

    [Fact]
    public void ClampSkipsWhenAlmostEntirelyOffScreen()
    {
        var rect = R(1880, 100, 2600, 700);

        Assert.Equal(rect, WindowSnapper.ClampToWorkArea(rect, R(0, 0, 1920, 1040)));
    }

    private static readonly Rectangle WorkArea = R(0, 0, 1920, 1040);

    [Fact]
    public void UnsnappedMoveOffTheRightSlidesBackAtSameSize()
    {
        var dropped = R(1500, 200, 2300, 800);

        var result = WindowSnapper.FitMoveToWorkArea(dropped, dropped, WorkArea);

        Assert.Equal(R(1120, 200, 1920, 800), result);
    }

    [Fact]
    public void UnsnappedMoveUnderTheTaskbarSlidesUp()
    {
        var dropped = R(100, 700, 900, 1300);

        var result = WindowSnapper.FitMoveToWorkArea(dropped, dropped, WorkArea);

        Assert.Equal(R(100, 440, 900, 1040), result);
    }

    [Fact]
    public void UnsnappedMoveOffTheLeftAndTopSlidesIn()
    {
        var dropped = R(-300, -50, 500, 550);

        Assert.Equal(R(0, 0, 800, 600), WindowSnapper.FitMoveToWorkArea(dropped, dropped, WorkArea));
    }

    [Fact]
    public void MoveWiderThanWorkAreaAlignsAndTrims()
    {
        var dropped = R(-100, 100, 2200, 600);

        Assert.Equal(R(0, 100, 1920, 600), WindowSnapper.FitMoveToWorkArea(dropped, dropped, WorkArea));
    }

    [Fact]
    public void MoveSnappedHorizontallyKeepsSnapAndTrims()
    {
        // Dropped 10px short of a neighbour's right edge at 960, snapped flush, far side off screen.
        var dropped = R(970, 200, 2370, 800);
        var snapped = R(960, 200, 2360, 800);

        Assert.Equal(R(960, 200, 1920, 800), WindowSnapper.FitMoveToWorkArea(dropped, snapped, WorkArea));
    }

    [Fact]
    public void MoveSnappedHorizontallyStillSlidesVertically()
    {
        var dropped = R(970, 700, 2370, 1300);
        var snapped = R(960, 700, 2360, 1300);

        Assert.Equal(R(960, 440, 1920, 1040), WindowSnapper.FitMoveToWorkArea(dropped, snapped, WorkArea));
    }

    [Fact]
    public void MoveAlreadyOnScreenIsUnchanged()
    {
        var dropped = R(100, 100, 900, 700);

        Assert.Equal(dropped, WindowSnapper.FitMoveToWorkArea(dropped, dropped, WorkArea));
    }

    [Fact]
    public void ResizeReleasedNearNeighbourSnapsFlush()
    {
        var start = R(0, 0, 800, 1040);
        var neighbour = R(960, 0, 1920, 1040);

        Assert.Equal(R(0, 0, 960, 1040), WindowSnapper.ComputeReleaseSnap(start, R(0, 0, 950, 1040), new[] { neighbour }));
        Assert.Equal(R(0, 0, 930, 1040), WindowSnapper.ComputeReleaseSnap(start, R(0, 0, 930, 1040), new[] { neighbour }));
    }

    [Fact]
    public void MoveReleasedNearNeighbourSnapsFlush()
    {
        var start = R(200, 100, 700, 600);
        var neighbour = R(960, 0, 1920, 1040);

        // Dropped with its right edge 12px short of the neighbour's left edge.
        var result = WindowSnapper.ComputeReleaseSnap(start, R(448, 100, 948, 600), new[] { neighbour });

        Assert.Equal(R(460, 100, 960, 600), result);
    }

    [Fact]
    public void NeighbourWithNoOverlapAlongTheEdgeIsIgnored()
    {
        var start = R(0, 0, 800, 500);
        var farBelow = R(960, 2000, 1920, 3000);

        var final = R(0, 0, 950, 500);

        Assert.Equal(final, WindowSnapper.ComputeReleaseSnap(start, final, new[] { farBelow }));
    }

    [Fact]
    public void ResizeOnlySnapsEdgesThatMoved()
    {
        // The left edge sits 10px from the neighbour's right edge but wasn't dragged.
        var start = R(970, 0, 1500, 1040);
        var neighbour = R(0, 0, 960, 1040);

        var final = R(970, 0, 1700, 1040);

        Assert.Equal(final, WindowSnapper.ComputeReleaseSnap(start, final, new[] { neighbour }));
    }
}
