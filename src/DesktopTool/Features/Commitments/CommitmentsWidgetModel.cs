using DesktopTool.UI;

namespace DesktopTool.Features.Commitments;

/// <summary>Persisted state for the on-screen Commitments widget (see UI.CommitmentsWidget) - same
/// shape as ClaudePipelineModel, minus RowsShown/AlwaysMaxRows: this widget's list simply fills
/// whatever body height it's been resized to. Only the widget's own look/position lives here - the
/// commitments themselves are CommitmentLog's own file.</summary>
public sealed class CommitmentsWidgetModel : WidgetStyleModel
{
    /// <summary>Null until the widget has actually been moved/resized once - see
    /// CommitmentsWidget's own CreateParams, which centers on the primary screen at a default size
    /// instead of guessing a fixed default that might not exist on every monitor layout.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 300;
    public int? Height { get; set; }

    public string Title { get; set; } = "Get Shit Done";

    /// <summary>Whether the widget should currently be showing - persisted so Widget Manager's
    /// toggle survives a restart instead of always defaulting back to shown.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>How many commitments one day accepts - kept small on purpose, so "everything I
    /// committed to is done" stays a finish line that can actually be reached.</summary>
    public int MaxPerDay { get; set; } = CommitmentLog.DefaultMaxPerDay;
}
