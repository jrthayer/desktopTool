using DesktopTool.UI;

namespace DesktopTool.Features.ClaudePipeline;

/// <summary>Persisted state for the on-screen Claude Pipeline widget (see UI.ClaudePipelineWidget) -
/// same shape as LayoutLauncherModel (a variable-length row list, so RowsShown/AlwaysMaxRows apply
/// here too), just for PipelineFeature rows instead of LayoutProfile rows.</summary>
public sealed class ClaudePipelineModel : WidgetStyleModel
{
    /// <summary>Null until the widget has actually been moved/resized once - see
    /// ClaudePipelineWidget's own CreateParams, which centers on the primary screen at a default size
    /// instead of guessing a fixed default that might not exist on every monitor layout.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 280;
    public int? Height { get; set; }

    public string Title { get; set; } = "Claude Toolbox";

    /// <summary>Whether the widget should currently be showing - persisted so the tray's/Widget
    /// Manager's toggle survives a restart instead of always defaulting back to shown.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>How many rows the list reserves body space for at most - same idea as
    /// LayoutLauncherModel.RowsShown, just against the current feature count instead of a saved-layout
    /// count.</summary>
    public int RowsShown { get; set; } = 5;

    /// <summary>While on, RowsShown is kept pinned to the current feature count - same knob as
    /// LayoutLauncherModel.AlwaysMaxRows.</summary>
    public bool AlwaysMaxRows { get; set; }
}
