using System.Drawing;
using System.Text.Json.Serialization;
using DesktopTool.UI;

namespace DesktopTool.Features.FolderFences;

/// <summary>Inherits WidgetStyleModel (TintColor/HeaderDarkness/Opacity/FullOpacityOnHover/
/// TintStrength/Margin/CornerRadius/TitleFontSize/TitleAlignment/HeaderBorderMode/LightBorder/
/// HideHeader/HeaderCloseButton) for the same reason FenceModel does - see that class's own doc
/// comment.</summary>
public sealed class FolderFenceModel : WidgetStyleModel
{
    // Capped at FolderFenceForm.CornerRadiusMax (20), unlike FenceModel's own 22 - the folder tab's
    // own proportions (see FolderFenceForm.GetTabWidth/TabExtraHeight) are sized for a more modest
    // range, so the default has to stay within that same ceiling too.
    public FolderFenceModel() : base(defaultCornerRadius: 20)
    {
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Folder Fence";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 240;
    public int Height { get; set; } = 200;

    /// <summary>The real folder this fence mirrors - null means the fence is still in its
    /// empty "+" state and hasn't been pointed at a folder yet (see FolderFenceForm.SetRootFolder).
    /// Unlike FenceModel.Files, nothing under this folder is ever individually stored here - the
    /// fence's contents are read live off disk every time (see FolderFenceForm's own grid).</summary>
    public string? RootFolderPath { get; set; }

    // Same two "fence additionals" FenceModel offers (see FenceForm.BuildAdditionalSettingsRows) -
    // HideLabels/OcdFenceSizing apply identically to a folder fence's own grid.
    public bool HideLabels { get; set; }
    public bool OcdFenceSizing { get; set; }

    [JsonIgnore]
    public Rectangle Bounds
    {
        get => new(X, Y, Width, Height);
        set
        {
            X = value.X;
            Y = value.Y;
            Width = value.Width;
            Height = value.Height;
        }
    }
}
