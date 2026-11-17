using System.Text;
using DesktopTool.Features.Layouts.Native;
using DesktopTool.Features.Snapping;
using DesktopTool.Native;

namespace DesktopTool.Features.WindowSnapping.Native;

/// <summary>Edge snapping for every normal top-level window on the desktop, not just this app's own:
///
/// - Snap on release: when a move or resize of a window ends with one of its edges within
///   Tolerance of a neighbouring window's edge, that edge is pulled flush to it.
/// - Linked resize: dragging an edge that sits flush with other windows' edges drags theirs along
///   with it, the way Windows Snap does for snapped windows. Windows only does that itself for
///   windows it considers snapped, and there's no public API to put a window into that state at an
///   arbitrary rect - so it's done here instead.
///
/// Nothing is remembered between drags. Each drag start snapshots the live geometry of every
/// snappable window and works out which of them touch the dragged one right then, so windows that
/// were pulled apart and put back flush are linked again automatically.
///
/// Cost: only the move/size start/end WinEvent is hooked while this exists (fires twice per drag of
/// any window). The location-change hook exists only for the duration of a drag on a snappable
/// window; it watches every process, since followers' own moves are how FollowRequest knows each
/// one has caught up, but anything that isn't the dragged window or a follower is a single
/// dictionary miss. All callbacks are WINEVENT_OUTOFCONTEXT, delivered
/// through the UI thread's own message loop - construct it on that thread.</summary>
internal sealed class WindowSnapper : IDisposable
{
    /// <summary>How far apart two edges can be and still count as one shared line, and how close an
    /// edge has to be released to a neighbour's to snap to it.</summary>
    public const int Tolerance = 16;

    /// <summary>Smallest width/height a following window is ever squeezed to.</summary>
    public const int MinSize = 100;

    private const uint FollowFlags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE
        | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_ASYNCWINDOWPOS;

    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;

    // Shell windows that pass every other test (visible, unowned, framed) but are never something
    // a person thinks of as a window to line up against: the desktop, the taskbars, and the Start/
    // Search/Action Center flyouts.
    private static readonly HashSet<string> ExcludedClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows.UI.Core.CoreWindow",
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    // Held in fields so the GC can't collect the delegates native code is still calling.
    private readonly WinEventProc _moveSizeProc;
    private readonly WinEventProc _locationProc;

    private IntPtr _moveSizeHook;
    private IntPtr _locationHook;
    private Drag? _drag;

    private sealed class Drag
    {
        public required IntPtr Window { get; init; }
        public required Rectangle Start { get; init; }
        public required RECT Insets { get; init; }
        public required List<Peer> Peers { get; init; }
        public required Dictionary<IntPtr, Peer> PeersByWindow { get; init; }
        public bool IsMove { get; set; }
    }

    private sealed class Peer
    {
        public required IntPtr Window { get; init; }
        public required Rectangle Start { get; init; }
        public required RECT Insets { get; init; }
        public required FollowRequest Request { get; init; }
    }

    public WindowSnapper()
    {
        _moveSizeProc = OnMoveSize;
        _locationProc = OnLocationChange;

        _moveSizeHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MOVESIZESTART, NativeMethods.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _moveSizeProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void OnMoveSize(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != NativeMethods.OBJID_WINDOW || hwnd == IntPtr.Zero)
            return;

        if (eventType == NativeMethods.EVENT_SYSTEM_MOVESIZESTART)
            BeginDrag(hwnd);
        else if (eventType == NativeMethods.EVENT_SYSTEM_MOVESIZEEND && _drag?.Window == hwnd)
            EndDrag();
    }

    private void BeginDrag(IntPtr hwnd)
    {
        // A previous drag whose end event never arrived (its window was destroyed mid-drag).
        if (_drag is not null)
            EndDrag();

        if (!IsSnappable(hwnd)
            || !WindowPlacer.TryGetVisibleFrame(hwnd, out var start)
            || !WindowPlacer.TryGetFrameInsets(hwnd, out var insets))
        {
            return;
        }

        var peers = SnapshotPeers(hwnd);
        _drag = new Drag
        {
            Window = hwnd, Start = ToRectangle(start), Insets = insets,
            Peers = peers, PeersByWindow = peers.ToDictionary(p => p.Window),
        };

        // Every process, not just the dragged window's: a follower's own location change is how we
        // know it has finished the last resize we asked of it (see FollowRequest). Only hooked for
        // the length of the drag, and the callback ignores anything that isn't one of ours.
        _locationHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _locationProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>Every other snappable window, skipping any that's completely covered by a single
    /// window above it - an edge you can't see shouldn't pull or be pulled. EnumWindows runs top to
    /// bottom in z-order, so everything already collected sits above the window being considered.</summary>
    private static List<Peer> SnapshotPeers(IntPtr dragged)
    {
        var peers = new List<Peer>();
        var above = new List<Rectangle>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == dragged || !IsSnappable(hWnd)
                || !WindowPlacer.TryGetVisibleFrame(hWnd, out var frame)
                || !WindowPlacer.TryGetFrameInsets(hWnd, out var insets))
            {
                return true;
            }

            var rect = ToRectangle(frame);
            if (!above.Any(a => a.Contains(rect)))
                peers.Add(new Peer { Window = hWnd, Start = rect, Insets = insets, Request = new FollowRequest(rect) });
            above.Add(rect);
            return true;
        }, IntPtr.Zero);

        return peers;
    }

    private void EndDrag()
    {
        var drag = _drag!;
        _drag = null;

        if (_locationHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        if (!NativeMethods.IsWindow(drag.Window) || !IsUsable(drag.Window))
            return;

        // One last look in case the location hook missed the final position.
        Follow(drag);

        if (!WindowPlacer.TryGetVisibleFrame(drag.Window, out var frame))
            return;

        var final = ToRectangle(frame);
        var snapped = ComputeReleaseSnap(drag.Start, final, drag.Peers.Select(p => p.Request.Wanted).ToList());

        // A window released hanging off the screen is brought back on: a moved one slides back
        // (trimmed only on an axis where it snapped to a neighbour), a resized one is trimmed. A
        // resize that trims the dragged edge moves its followers along below.
        var workArea = Screen.FromRectangle(snapped).WorkingArea;
        snapped = IsMove(drag.Start, final)
            ? FitMoveToWorkArea(final, snapped, workArea)
            : ClampToWorkArea(snapped, workArea);

        if (snapped != final)
        {
            SetFrame(drag.Window, snapped, drag.Insets);

            // A resize that snapped an edge onto a line other windows were following moves them too.
            if (!drag.IsMove)
            {
                var targets = ComputeFollowerRects(final, snapped, drag.Peers.Select(p => p.Request.Wanted).ToList());
                ApplyTargets(drag.Peers, targets);
            }
        }

        // Every follower's last-wanted rect goes out now, even to one still busy with an earlier
        // request - at most one is ever in flight, so this is the last one it handles.
        foreach (var peer in drag.Peers)
        {
            if (peer.Request.Flush() && NativeMethods.IsWindow(peer.Window))
                SetFrame(peer.Window, peer.Request.Sent, peer.Insets);
        }
    }

    private void OnLocationChange(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != NativeMethods.OBJID_WINDOW || _drag is not { } drag)
            return;

        if (hwnd == drag.Window)
        {
            Follow(drag);
        }
        else if (drag.PeersByWindow.TryGetValue(hwnd, out var peer)
            && peer.Request.Acknowledge(Environment.TickCount64))
        {
            // The follower finished its last resize and the edge has moved on since: send it
            // straight to where it should be now, skipping everything in between.
            SetFrame(peer.Window, peer.Request.Sent, peer.Insets);
        }
    }

    private static void Follow(Drag drag)
    {
        if (drag.IsMove || drag.Peers.Count == 0 || !WindowPlacer.TryGetVisibleFrame(drag.Window, out var frame))
            return;

        var current = ToRectangle(frame);
        if (IsMove(drag.Start, current))
        {
            // A title-bar drag: nothing follows, and any follower already nudged by an earlier
            // location event (can't really happen for a pure move, but cheap to undo) goes back.
            drag.IsMove = true;
            ApplyTargets(drag.Peers, drag.Peers.Select(p => p.Start).ToArray());
            return;
        }

        var targets = ComputeFollowerRects(drag.Start, current, drag.Peers.Select(p => p.Start).ToList());
        ApplyTargets(drag.Peers, targets);
    }

    private static void ApplyTargets(List<Peer> peers, IReadOnlyList<Rectangle> targets)
    {
        var now = Environment.TickCount64;
        for (var i = 0; i < peers.Count; i++)
        {
            var peer = peers[i];
            if (peer.Request.Want(targets[i], now) && NativeMethods.IsWindow(peer.Window))
                SetFrame(peer.Window, peer.Request.Sent, peer.Insets);
        }
    }

    private static void SetFrame(IntPtr hwnd, Rectangle frame, RECT insets) =>
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            frame.X - insets.Left,
            frame.Y - insets.Top,
            frame.Width + insets.Left + insets.Right,
            frame.Height + insets.Top + insets.Bottom,
            FollowFlags);

    /// <summary>A real, resizable-or-captioned top-level window the user can see and that's in its
    /// normal (not minimized/maximized) state, and isn't shell chrome or one of this app's own.
    /// Called for every window on every drag start, so it stays cheap - no exe path lookup.</summary>
    private static bool IsSnappable(IntPtr hwnd)
    {
        if (!IsUsable(hwnd)
            || NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero
            || WindowPlacer.IsCloaked(hwnd))
        {
            return false;
        }

        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        if ((style & (WS_CAPTION | WS_THICKFRAME)) == 0)
            return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId)
            return false;

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return !ExcludedClassNames.Contains(className.ToString());
    }

    private static bool IsUsable(IntPtr hwnd) =>
        NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd)
        && !NativeMethods.IsIconic(hwnd) && !NativeMethods.IsZoomed(hwnd);

    private static Rectangle ToRectangle(RECT r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>Where the dragged window should end up once released at final, having started the
    /// drag at start, given the other windows' frames. A move snaps as a whole (SnapEngine.SnapMove);
    /// a resize snaps only the edges that actually moved (SnapEngine.SnapResize). An edge only snaps
    /// to windows it actually faces: vertical candidates (Left/Right x values) come only from windows
    /// whose vertical extent overlaps the dragged window's, and horizontal ones likewise - so a window
    /// at the far top of the screen can't tug on the left edge of one at the bottom.</summary>
    internal static Rectangle ComputeReleaseSnap(Rectangle start, Rectangle final, IReadOnlyList<Rectangle> others,
        int tolerance = Tolerance)
    {
        var vertical = new List<int>();
        var horizontal = new List<int>();
        foreach (var o in others)
        {
            if (o.Top <= final.Bottom + tolerance && final.Top <= o.Bottom + tolerance)
            {
                vertical.Add(o.Left);
                vertical.Add(o.Right);
            }

            if (o.Left <= final.Right + tolerance && final.Left <= o.Right + tolerance)
            {
                horizontal.Add(o.Top);
                horizontal.Add(o.Bottom);
            }
        }

        if (start == final || (vertical.Count == 0 && horizontal.Count == 0))
            return final;

        if (IsMove(start, final))
            return SnapEngine.SnapMove(final, vertical, horizontal, tolerance).Rect;

        var edges = SnapEdges.None;
        if (final.Left != start.Left) edges |= SnapEdges.Left;
        if (final.Right != start.Right) edges |= SnapEdges.Right;
        if (final.Top != start.Top) edges |= SnapEdges.Top;
        if (final.Bottom != start.Bottom) edges |= SnapEdges.Bottom;

        return SnapEngine.SnapResize(final, edges, vertical, horizontal, tolerance).Rect;
    }

    /// <summary>Trims rect so none of it hangs outside workArea (the monitor's working area, so the
    /// taskbar counts as off-screen too). Applied to every released resize, after the release snap:
    /// edges already on screen (a snapped one included) stay put, and only the ones off-screen move
    /// in. Left alone if trimming would leave less than minSize either way, which only happens when
    /// the window is almost entirely off-screen.</summary>
    internal static Rectangle ClampToWorkArea(Rectangle rect, Rectangle workArea, int minSize = MinSize)
    {
        var clamped = Rectangle.Intersect(rect, workArea);
        return clamped.Width >= minSize && clamped.Height >= minSize ? clamped : rect;
    }

    /// <summary>Brings a released move back on screen, each axis on its own. dropped is where the
    /// window was let go, snapped where the release snap put it.
    ///
    /// An axis where it snapped to a neighbour (its position changed in the snap) is trimmed, as
    /// ClampToWorkArea does: sliding would pull the snapped edge off the neighbour's and overlap
    /// them. Any other axis slides back onto the nearest working-area edge at the same size. A
    /// window bigger than the working area on that axis lines up with its near edge and the rest is
    /// trimmed.</summary>
    internal static Rectangle FitMoveToWorkArea(Rectangle dropped, Rectangle snapped, Rectangle workArea,
        int minSize = MinSize)
    {
        var (left, right) = FitAxis(snapped.Left, snapped.Right, workArea.Left, workArea.Right,
            trim: snapped.X != dropped.X, minSize);
        var (top, bottom) = FitAxis(snapped.Top, snapped.Bottom, workArea.Top, workArea.Bottom,
            trim: snapped.Y != dropped.Y, minSize);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static (int Near, int Far) FitAxis(int near, int far, int areaNear, int areaFar, bool trim, int minSize)
    {
        if (near >= areaNear && far <= areaFar)
            return (near, far);

        if (trim)
        {
            var trimmedNear = Math.Max(near, areaNear);
            var trimmedFar = Math.Min(far, areaFar);
            return trimmedFar - trimmedNear >= minSize ? (trimmedNear, trimmedFar) : (near, far);
        }

        var size = far - near;
        if (size >= areaFar - areaNear)
            return (areaNear, areaFar);
        if (near < areaNear)
            return (areaNear, areaNear + size);
        return (areaFar - size, areaFar);
    }

    /// <summary>Whether the dragged window went from start to current by moving (title-bar drag)
    /// rather than resizing - every edge shifted by the same amount, and it actually went somewhere.</summary>
    internal static bool IsMove(Rectangle start, Rectangle current) =>
        current.Left - start.Left == current.Right - start.Right
        && current.Top - start.Top == current.Bottom - start.Bottom
        && current.Location != start.Location;

    /// <summary>Where every other window (by their positions at drag start) should be, given the
    /// dragged window went from start to current by resizing. Each axis is worked out independently:
    /// a vertical line moves when exactly one of Left/Right changed, a horizontal one when exactly one
    /// of Top/Bottom did (a corner drag moves one of each).</summary>
    internal static Rectangle[] ComputeFollowerRects(Rectangle start, Rectangle current, IReadOnlyList<Rectangle> others)
    {
        var vertical = others.ToArray();
        if (current.Left != start.Left && current.Right == start.Right)
            vertical = ComputeLinkedRects(start, verticalLine: true, start.Left, current.Left, others);
        else if (current.Right != start.Right && current.Left == start.Left)
            vertical = ComputeLinkedRects(start, verticalLine: true, start.Right, current.Right, others);

        var horizontal = others.ToArray();
        if (current.Top != start.Top && current.Bottom == start.Bottom)
            horizontal = ComputeLinkedRects(start, verticalLine: false, start.Top, current.Top, others);
        else if (current.Bottom != start.Bottom && current.Top == start.Top)
            horizontal = ComputeLinkedRects(start, verticalLine: false, start.Bottom, current.Bottom, others);

        var result = new Rectangle[others.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = Rectangle.FromLTRB(vertical[i].Left, horizontal[i].Top, vertical[i].Right, horizontal[i].Bottom);
        return result;
    }

    /// <summary>Moves every window with an edge on the dragged line from linePos to newPos.
    /// verticalLine means a left/right edge (an x coordinate); otherwise a top/bottom one (y).
    ///
    /// A window is on the line when its near edge is within tolerance of linePos and it lies wholly
    /// to one side of it - "before" (left of/above: its right/bottom edge is on the line) or "after"
    /// (its left/top edge is). Only windows connected to the dragged one along the line count: the
    /// dragged window's span along the line (its vertical extent for a vertical line) seeds a set,
    /// and any on-line window whose span touches one already in the set joins it, repeatedly - so
    /// in a 2x2 grid, dragging one window's inner edge moves the whole centre line through all four,
    /// while an unrelated window that merely shares the same x elsewhere on screen is left alone.
    ///
    /// Before-windows get their right/bottom edge set to newPos, after-windows their left/top edge,
    /// never shrinking below minSize. Returns a new array, one entry per input, unchanged ones as-is.</summary>
    internal static Rectangle[] ComputeLinkedRects(Rectangle dragged, bool verticalLine, int linePos, int newPos,
        IReadOnlyList<Rectangle> others, int tolerance = Tolerance, int minSize = MinSize)
    {
        var result = others.ToArray();
        if (linePos == newPos)
            return result;

        int Near(Rectangle r) => verticalLine ? r.Left : r.Top;   // lower coordinate edge
        int Far(Rectangle r) => verticalLine ? r.Right : r.Bottom; // higher coordinate edge
        (int Start, int End) Span(Rectangle r) => verticalLine ? (r.Top, r.Bottom) : (r.Left, r.Right);

        // 1 = before the line (its Far edge is on it), 2 = after it (its Near edge is).
        var side = new int[others.Count];
        for (var i = 0; i < others.Count; i++)
        {
            var r = others[i];
            if (Math.Abs(Far(r) - linePos) <= tolerance && Near(r) < linePos)
                side[i] = 1;
            else if (Math.Abs(Near(r) - linePos) <= tolerance && Far(r) > linePos)
                side[i] = 2;
        }

        var spans = new List<(int Start, int End)> { Span(dragged) };
        var linked = new bool[others.Count];
        bool grew;
        do
        {
            grew = false;
            for (var i = 0; i < others.Count; i++)
            {
                if (linked[i] || side[i] == 0)
                    continue;

                var span = Span(others[i]);
                if (!spans.Any(s => span.Start <= s.End + tolerance && s.Start <= span.End + tolerance))
                    continue;

                linked[i] = true;
                spans.Add(span);
                grew = true;
            }
        }
        while (grew);

        for (var i = 0; i < others.Count; i++)
        {
            if (!linked[i])
                continue;

            var r = others[i];
            if (side[i] == 1)
            {
                var far = Math.Max(newPos, Near(r) + minSize);
                result[i] = verticalLine
                    ? Rectangle.FromLTRB(r.Left, r.Top, far, r.Bottom)
                    : Rectangle.FromLTRB(r.Left, r.Top, r.Right, far);
            }
            else
            {
                var near = Math.Min(newPos, Far(r) - minSize);
                result[i] = verticalLine
                    ? Rectangle.FromLTRB(near, r.Top, r.Right, r.Bottom)
                    : Rectangle.FromLTRB(r.Left, near, r.Right, r.Bottom);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_locationHook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_locationHook);
        if (_moveSizeHook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_moveSizeHook);
        _locationHook = _moveSizeHook = IntPtr.Zero;
        _drag = null;
    }
}
