namespace DesktopTool.Features.WindowSnapping.Native;

/// <summary>Paces the resize requests WindowSnapper sends one follower window during a drag.
///
/// Followers are moved with SetWindowPos(SWP_ASYNCWINDOWPOS), which posts the request to the
/// follower app's own thread rather than moving it there and then. The dragged edge reports a new
/// position every few milliseconds, faster than a heavy app (a browser, an editor) can re-lay-out
/// and repaint. Posting every one of them queues up stale sizes that the app then works through in
/// order, so it falls further and further behind and keeps moving after the drag ends.
///
/// So at most one request is ever in flight. A new target is only sent once the follower has
/// acknowledged the previous one (any location change of its own - not "reached the exact rect",
/// since an app with a minimum size never would), and then it's always the newest target, skipping
/// everything in between. An app that never acknowledges (busy, hung) is sent the newest target
/// again after AckTimeoutMs anyway. Pure state, clock passed in, so it can be unit-tested.</summary>
internal sealed class FollowRequest
{
    public const long AckTimeoutMs = 100;

    private long? _awaitingSince;

    public FollowRequest(Rectangle current)
    {
        Wanted = Sent = current;
    }

    /// <summary>Where the follower should be now.</summary>
    public Rectangle Wanted { get; private set; }

    /// <summary>The last rect actually posted to the follower - what the caller sends whenever one of
    /// the methods below returns true.</summary>
    public Rectangle Sent { get; private set; }

    public bool IsAwaiting => _awaitingSince is not null;

    /// <summary>Records a new target. True when it should be sent right now.</summary>
    public bool Want(Rectangle target, long now)
    {
        Wanted = target;
        return TrySend(now);
    }

    /// <summary>The follower has moved, so it's done with the last request. True when the target
    /// has moved on since and should be sent right now.</summary>
    public bool Acknowledge(long now)
    {
        _awaitingSince = null;
        return TrySend(now);
    }

    /// <summary>End of the drag: send the newest target even while awaiting. True if there's
    /// anything new to send.</summary>
    public bool Flush()
    {
        if (Wanted == Sent)
            return false;

        Sent = Wanted;
        _awaitingSince = null;
        return true;
    }

    private bool TrySend(long now)
    {
        if (Wanted == Sent)
            return false;
        if (_awaitingSince is { } since && now - since < AckTimeoutMs)
            return false;

        Sent = Wanted;
        _awaitingSince = now;
        return true;
    }
}
