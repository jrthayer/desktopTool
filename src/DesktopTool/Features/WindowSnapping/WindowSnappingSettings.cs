namespace DesktopTool.Features.WindowSnapping;

/// <summary>Persisted state for Window Snapping (see WindowSnappingService) - just the Widget
/// Manager switch for now. The layout tools will move in here later.</summary>
public sealed class WindowSnappingSettings
{
    public bool Enabled { get; set; } = true;
}
