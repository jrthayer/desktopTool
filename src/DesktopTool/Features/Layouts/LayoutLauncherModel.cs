using DesktopTool.UI;

namespace DesktopTool.Features.Layouts;

/// <summary>Persisted state for the on-screen Layout Launcher widget (see
/// UI.LayoutLauncherWidget) - inherits WidgetStyleModel for the per-element styling knobs shared
/// with every other widget on this base (TintColor/HeaderDarkness/Opacity/FullOpacityOnHover/
/// TintStrength/Margin/CornerRadius/etc - see IWidgetStyle), adding this widget's own position/
/// size/title/visibility. Unlike FenceModel there's no Id or Files list - only one of these ever
/// exists, so LayoutLauncherStore persists a single object rather than a collection the way
/// FenceStore/LayoutStore do.</summary>
public sealed class LayoutLauncherModel : WidgetStyleModel
{
    /// <summary>Null until the widget has actually been moved/resized once - see
    /// LayoutLauncherWidget's own CreateParams, which centers on the primary screen at a default size
    /// instead of guessing a fixed default that might not exist on every monitor layout.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 280;
    public int? Height { get; set; }

    public string Title { get; set; } = "Layout Launcher";

    /// <summary>Whether the widget should currently be showing - persisted so the tray's "Layout
    /// Launcher" toggle survives a restart instead of always defaulting back to shown.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>How many rows the list reserves body space for at most (see LayoutLauncherWidget.
    /// GetListArea) - fewer saved profiles than this just leaves blank space below the list rather
    /// than stretching it; more than this scrolls instead of growing further, even if the body itself
    /// is taller. A direct +/- setting instead of the resize-drag-snapping this replaced, since sizing
    /// the list by row count is the actual thing being asked for, not a resize-interaction nicety.</summary>
    public int RowsShown { get; set; } = 5;

    /// <summary>While on, RowsShown is kept pinned to the current saved-layout count at all times
    /// (see LayoutLauncherWidget.SyncRowsShownToMax) - turning it on, and every layout this widget
    /// itself adds or removes afterward (Save Current Layout, a row's own Copy/Delete), re-syncs
    /// RowsShown (and so the widget's own size - see SetRowsShown/ResizeBodyHeight) to match.</summary>
    public bool AlwaysMaxRows { get; set; }

    /// <summary>While on (and only while AlwaysMaxRows is too - it's only offered then), the widget
    /// can't be resized shorter than it takes to show every saved layout, and grows to that height
    /// whenever it's shorter (a layout was just saved, say) - see LayoutLauncherWidget.
    /// EnforceMinHeight/ComputeResizedBody.</summary>
    public bool MinHeightFitsAllRows { get; set; }
}
