using DesktopTool.Features.Fences.UI;
using DesktopTool.Features.Snapping;

namespace DesktopTool.Features.Fences;

/// <summary>
/// Owns the persisted set of custom snap lines, and orchestrates the windows involved: a single
/// SnapGuideOverlay shown only for the duration of a live fence drag (custom lines plus whichever
/// other-fence edges are currently snapped, highlighted) and hidden the instant it ends, and the
/// SnapLineEditOverlay/SnapLinePanel pair for "Manage Snap Lines..." edit mode. Geometry itself
/// lives entirely in the stateless SnapEngine - this class only gathers candidates, persists state,
/// and wires the UI pieces together.
/// </summary>
public sealed class SnapLineManager : IDisposable
{
    private readonly SnapLineStore _store = new();
    private readonly List<SnapLineModel> _lines;
    private readonly HashSet<string> _seededMonitors;

    private SnapGuideOverlay? _guideOverlay;
    private SnapLineEditOverlay? _editOverlay;
    private SnapLinePanel? _editPanel;

    public IReadOnlyList<SnapLineModel> Lines => _lines;

    /// <summary>Off drops every custom snap line from drag candidates app-wide - see
    /// SnapLineSettings.Enabled and SetEnabled below. Loaded once here; every other place that
    /// cares (MergeCandidates/BeginDrag/UpdateDragOverlay) reads this field directly rather than
    /// re-deriving it.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Off drops every other live widget's edges from drag/resize candidates app-wide - see
    /// LayeredWidgetForm.GetOtherWidgetEdges, which is the sole place that reads this. Loaded once
    /// here, same as Enabled above.</summary>
    public bool WidgetEdgesEnabled { get; private set; }

    public event Action? LinesChanged;

    public SnapLineManager()
    {
        var settings = _store.Load();
        _lines = settings.Lines;
        _seededMonitors = settings.SeededMonitors;
        Enabled = settings.Enabled;
        WidgetEdgesEnabled = settings.WidgetEdgesEnabled;
        SeedDefaultEdgeLinesForNewMonitors();
    }

    /// <summary>Widget Manager's own Snap Lines switch - a no-op if it didn't actually change, same
    /// redundant-but-safe guard every other setter-style mutator here already has.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == Enabled)
            return;
        Enabled = enabled;
        Save();
    }

    /// <summary>Widget Manager's own Widget Snapping switch - same shape as SetEnabled above.</summary>
    public void SetWidgetEdgesEnabled(bool enabled)
    {
        if (enabled == WidgetEdgesEnabled)
            return;
        WidgetEdgesEnabled = enabled;
        Save();
    }

    /// <summary>Gives every monitor that's never been seeded before (a first-ever launch, or a
    /// monitor connected for the first time since) a default Top/Bottom/Left/Right line flush with
    /// its own working-area edges (excluding the taskbar, same reasoning as SnapLinePanel's own
    /// Position field) - a ready-to-use baseline without the user having to draw them out manually.
    /// Never re-seeds a monitor it's already given the chance to (tracked in _seededMonitors,
    /// regardless of whether the user went on to delete some or all of them), so a deletion always
    /// sticks.</summary>
    private void SeedDefaultEdgeLinesForNewMonitors()
    {
        var added = false;
        foreach (var screen in Screen.AllScreens)
        {
            if (!_seededMonitors.Add(screen.DeviceName))
                continue;

            var area = screen.WorkingArea;
            _lines.Add(new SnapLineModel { Orientation = SnapOrientation.Horizontal, Position = area.Top, MonitorBounds = screen.Bounds });
            _lines.Add(new SnapLineModel { Orientation = SnapOrientation.Horizontal, Position = area.Bottom, MonitorBounds = screen.Bounds });
            _lines.Add(new SnapLineModel { Orientation = SnapOrientation.Vertical, Position = area.Left, MonitorBounds = screen.Bounds });
            _lines.Add(new SnapLineModel { Orientation = SnapOrientation.Vertical, Position = area.Right, MonitorBounds = screen.Bounds });
            added = true;
        }

        if (added)
            Save();
    }

    public SnapLineModel Add(SnapOrientation orientation, int position, Rectangle monitorBounds)
    {
        var line = new SnapLineModel { Orientation = orientation, Position = position, MonitorBounds = monitorBounds };
        _lines.Add(line);
        Save();
        LinesChanged?.Invoke();
        return line;
    }

    /// <summary>monitorBounds/orientation are left null for a plain position edit (e.g. dragging
    /// the line directly only ever changes its position, never its orientation) - only the corner
    /// box's Update passes both, since its Screen combo and orientation radios can each be changed
    /// independently of the position field.</summary>
    public void Update(Guid id, int position, Rectangle? monitorBounds = null, SnapOrientation? orientation = null)
    {
        var line = _lines.FirstOrDefault(l => l.Id == id);
        if (line is null)
            return;
        line.Position = position;
        if (monitorBounds is { } bounds)
            line.MonitorBounds = bounds;
        if (orientation is { } newOrientation)
            line.Orientation = newOrientation;
        Save();
        LinesChanged?.Invoke();
    }

    public void Delete(Guid id)
    {
        var line = _lines.FirstOrDefault(l => l.Id == id);
        if (line is null || !_lines.Remove(line))
            return;
        Save();
        LinesChanged?.Invoke();
    }

    /// <summary>Shows the guide overlay for the duration of a drag, starting with just the plain
    /// (nothing highlighted yet) lines - SnapMove/SnapResize update it from there as candidates are
    /// actually snapped to. includeCustomLines true (the native left-button drag) shows this
    /// fence's own custom lines; false (the fence-edge-only right-button drag, see
    /// FenceForm.BeginRightDrag) shows verticalGuides/horizontalGuides instead - the other fences'
    /// edges, passed in by the caller since this class has no notion of "other fences" itself -
    /// drawn spanning guideSpan (the dragged fence's own monitor).</summary>
    public void BeginDrag(bool includeCustomLines = true, IReadOnlyList<int>? verticalGuides = null, IReadOnlyList<int>? horizontalGuides = null, Rectangle guideSpan = default)
    {
        _guideOverlay ??= new SnapGuideOverlay();

        var showCustomLines = includeCustomLines && Enabled;
        var lines = showCustomLines
            ? _lines.Select(l => (l.Orientation, l.Position, Highlighted: false, Span: MonitorSpanOf(l))).ToList()
            : new List<(SnapOrientation Orientation, int Position, bool Highlighted, Rectangle Span)>();

        if (!showCustomLines)
        {
            if (verticalGuides is not null)
                lines.AddRange(verticalGuides.Distinct().Select(p => (SnapOrientation.Vertical, p, false, guideSpan)));
            if (horizontalGuides is not null)
                lines.AddRange(horizontalGuides.Distinct().Select(p => (SnapOrientation.Horizontal, p, false, guideSpan)));
        }

        _guideOverlay.SetLines(lines);
        _guideOverlay.Show();
    }

    /// <summary>margin is the dragged fence's own FenceModel.Margin - applied to custom line
    /// candidates the exact same way FenceManager.GetOtherFenceEdges already applies it to other
    /// fences' edges, so a fence with a margin set keeps that same gap away from a custom snap line
    /// too, not just from other fences. includeCustomLines false (see BeginDrag) drops this fence's
    /// own custom lines from the merge entirely, leaving only whatever candidates the caller passed
    /// in directly (FenceForm's right-button drag passes just the other fences' edges) - splitting
    /// the two candidate sources across the two buttons instead of always merging them, which made
    /// dragging feel "sticky" with several fences and lines on screen at once.</summary>
    public SnapResult SnapMove(Rectangle proposedBody, IReadOnlyList<int> verticalCandidates, IReadOnlyList<int> horizontalCandidates, int margin, bool includeCustomLines = true)
    {
        var monitor = Screen.FromRectangle(proposedBody).Bounds;
        var (vCandidates, hCandidates) = MergeCandidates(monitor, margin, verticalCandidates, horizontalCandidates, includeCustomLines);
        var result = SnapEngine.SnapMove(proposedBody, vCandidates, hCandidates);
        UpdateDragOverlay(result, monitor, margin, includeCustomLines, verticalCandidates, horizontalCandidates);
        return result;
    }

    public SnapResult SnapResize(Rectangle proposedBody, SnapEdges activeEdges, IReadOnlyList<int> verticalCandidates, IReadOnlyList<int> horizontalCandidates, int margin)
    {
        var monitor = Screen.FromRectangle(proposedBody).Bounds;
        var (vCandidates, hCandidates) = MergeCandidates(monitor, margin, verticalCandidates, horizontalCandidates, includeCustomLines: true);
        var result = SnapEngine.SnapResize(proposedBody, activeEdges, vCandidates, hCandidates);
        UpdateDragOverlay(result, monitor, margin, includeCustomLines: true, verticalCandidates, horizontalCandidates);
        return result;
    }

    public void EndDrag() => _guideOverlay?.Hide();

    public void EnterEditMode()
    {
        if (_editOverlay is not null)
        {
            _editPanel?.Activate();
            return;
        }

        var overlay = new SnapLineEditOverlay(this);
        var panel = new SnapLinePanel();
        _editOverlay = overlay;
        _editPanel = panel;

        overlay.LineSelected += (id, _) =>
        {
            var line = _lines.FirstOrDefault(l => l.Id == id);
            if (line is not null)
                panel.PopulateFrom(line);
        };
        overlay.LineDragged += (id, position, monitorBounds) =>
        {
            Update(id, position, monitorBounds);
            var line = _lines.FirstOrDefault(l => l.Id == id);
            if (line is not null)
                panel.PopulateFrom(line); // keep the box's field live as the line is dragged directly
        };
        overlay.LineDeleteRequested += id =>
        {
            Delete(id);
            panel.ClearSelection();
        };
        overlay.NewLineCommitted += (orientation, position, monitorBounds) => Add(orientation, position, monitorBounds);
        overlay.CloseRequested += ExitEditMode;

        // The corner box now has its own explicit Screen field (defaulting to the primary
        // monitor), so both Add and Update pass along whatever screen is currently selected there.
        panel.AddRequested += (orientation, position, monitorBounds) => Add(orientation, position, monitorBounds);
        panel.UpdateRequested += (id, orientation, position, monitorBounds) => Update(id, position, monitorBounds, orientation);
        panel.DeleteRequested += id =>
        {
            Delete(id);
            panel.ClearSelection();
        };
        panel.NewLineRequested += overlay.ClearSelection;
        panel.CloseRequested += ExitEditMode;

        // The panel is a normal, user-draggable window (FixedToolWindow, not locked in place) - the
        // overlay's excluded region (see SnapLineEditOverlay.ExcludeScreenRect) has to keep tracking
        // wherever it currently is, or the panel becomes unclickable again as soon as it's moved
        // away from where it was first shown. The _editOverlay == overlay check guards against a
        // stray LocationChanged firing during this exact overlay/panel pair's own teardown in
        // ExitEditMode, which nulls _editOverlay before closing either window.
        panel.LocationChanged += (_, _) =>
        {
            if (_editOverlay == overlay)
                overlay.ExcludeScreenRect(panel.Bounds);
        };

        overlay.Show();
        panel.PositionTopRight();
        panel.Show();
        overlay.ExcludeScreenRect(panel.Bounds);
    }

    public void ExitEditMode()
    {
        if (_editOverlay is null)
            return;

        // Nulled out before closing - Close() synchronously re-enters here via each window's own
        // CloseRequested (SnapLinePanel's native caption close button, SnapLineEditOverlay's
        // Escape), and this guard is what stops that from double-disposing.
        var overlay = _editOverlay;
        var panel = _editPanel;
        _editOverlay = null;
        _editPanel = null;

        overlay!.Close();
        overlay.Dispose();
        panel!.Close();
        panel.Dispose();
    }

    public void Dispose()
    {
        ExitEditMode();
        _guideOverlay?.Close();
        _guideOverlay?.Dispose();
    }

    /// <summary>extraVertical/extraHorizontal are the same raw (pre-merge) candidates SnapMove/
    /// SnapResize were called with - only actually used when includeCustomLines is false, as the
    /// full set of passive guide lines to keep showing every tick (not just whichever of them
    /// happen to be snapped right now - same "shown the whole time, highlighted when matched"
    /// treatment BeginDrag already gives the custom lines in the includeCustomLines-true case, see
    /// its own comment), letting a right-button drag show every other fence's edge as a guide, not
    /// just the one it's currently snapped to.
    ///
    /// margin gets each custom line its own pair of extra guide-line entries at Position-margin/
    /// Position+margin, mirroring GetOtherFenceEdges' own margin-offset entries in extraVertical/
    /// extraHorizontal below (each shown and highlighted as its own line, not folded into the flush
    /// one) - without this, a fence that snapped to one of MergeCandidates' own margin-offset
    /// candidates (see its own comment - those candidates are real, already being offered) settled
    /// margin pixels away from the line with no guide there and the line itself never highlighting,
    /// reading as "the margin isn't doing anything" even though the snap itself was working.</summary>
    private void UpdateDragOverlay(SnapResult result, Rectangle monitor, int margin, bool includeCustomLines, IReadOnlyList<int> extraVertical, IReadOnlyList<int> extraHorizontal)
    {
        _guideOverlay ??= new SnapGuideOverlay();

        var vSnapped = new HashSet<int>(result.SnappedVerticalPositions);
        var hSnapped = new HashSet<int>(result.SnappedHorizontalPositions);

        // Built as a union of two independent sources rather than an either/or branch, so a drag
        // that's combining both buttons' candidates (see FenceForm's own MouseButtons check in
        // WM_MOVING/UpdateRightDrag) shows both sets of guide lines together, not just whichever
        // one this call happened to lead with.
        var showCustomLines = includeCustomLines && Enabled;
        var lines = new List<(SnapOrientation Orientation, int Position, bool Highlighted, Rectangle Span)>();
        if (showCustomLines)
        {
            foreach (var l in _lines)
            {
                var span = MonitorSpanOf(l);
                var snapped = l.Orientation == SnapOrientation.Horizontal ? hSnapped : vSnapped;
                lines.Add((l.Orientation, l.Position, snapped.Contains(l.Position), span));
                if (margin > 0)
                {
                    lines.Add((l.Orientation, l.Position - margin, snapped.Contains(l.Position - margin), span));
                    lines.Add((l.Orientation, l.Position + margin, snapped.Contains(l.Position + margin), span));
                }
            }
        }

        var customPositions = showCustomLines
            ? _lines.Select(l => (l.Orientation, l.Position)).ToHashSet()
            : new HashSet<(SnapOrientation, int)>();

        // extraVertical/extraHorizontal are the caller's own additional candidates (fence edges,
        // for the right-button drag) - skipped only when they duplicate a custom line already
        // listed above (possible once both sources are combined), never dropped just because
        // includeCustomLines happens to be true otherwise.
        foreach (var position in extraVertical.Distinct())
            if (!customPositions.Contains((SnapOrientation.Vertical, position)))
                lines.Add((SnapOrientation.Vertical, position, vSnapped.Contains(position), monitor));
        foreach (var position in extraHorizontal.Distinct())
            if (!customPositions.Contains((SnapOrientation.Horizontal, position)))
                lines.Add((SnapOrientation.Horizontal, position, hSnapped.Contains(position), monitor));

        _guideOverlay.SetLines(lines);
    }

    /// <summary>A line saved before per-monitor scoping existed has a zero-size MonitorBounds -
    /// treat that as unscoped (full virtual screen) rather than an invisible zero-width line.</summary>
    private static Rectangle MonitorSpanOf(SnapLineModel line)
    {
        var bounds = line.MonitorBounds;
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : SystemInformation.VirtualScreen;
    }

    /// <summary>Only a custom line whose own monitor matches the one currently being dragged over is
    /// offered as a snap candidate - a line drawn on monitor A shouldn't reach across and snap a
    /// fence being moved on monitor B. A legacy zero-size (pre-scoping) line still applies
    /// everywhere, matching its old unscoped behavior.
    ///
    /// When margin is set, each line also contributes two candidates offset by that amount on
    /// either side (line.Position - margin and + margin) alongside the flush one - unlike a fence's
    /// own edge (which only makes sense padded outward, away from its own span - see
    /// FenceManager.GetOtherFenceEdges), a standalone line has no "interior" to avoid overlapping,
    /// so both directions are equally valid depending on which side the fence approaches from.</summary>
    private (List<int> Vertical, List<int> Horizontal) MergeCandidates(Rectangle monitor, int margin, IReadOnlyList<int> extraVertical, IReadOnlyList<int> extraHorizontal, bool includeCustomLines)
    {
        var vertical = new List<int>(extraVertical);
        var horizontal = new List<int>(extraHorizontal);

        if (!includeCustomLines || !Enabled)
            return (vertical, horizontal);

        foreach (var line in _lines)
        {
            var bounds = line.MonitorBounds;
            var isScoped = bounds.Width > 0 && bounds.Height > 0;
            if (isScoped && bounds != monitor)
                continue;

            var target = line.Orientation == SnapOrientation.Vertical ? vertical : horizontal;
            target.Add(line.Position);
            if (margin > 0)
            {
                target.Add(line.Position - margin);
                target.Add(line.Position + margin);
            }
        }

        return (vertical, horizontal);
    }

    private void Save() => _store.Save(new SnapLineSettings { Lines = _lines, SeededMonitors = _seededMonitors, Enabled = Enabled, WidgetEdgesEnabled = WidgetEdgesEnabled });
}
