using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopTool.UI;

namespace DesktopTool.Features.Fences;

/// <summary>A file/shortcut a fence holds a reference to. DisplayName, when set, overrides the
/// label shown in the fence (renaming here never touches the real file on disk).</summary>
public sealed class FenceItem
{
    /// <summary>Wherever the file currently lives on disk - its original location, unless
    /// RealDesktopPath is set, in which case DesktopIconHider has relocated it into its hidden
    /// folder (see that class's own doc comment) and this points at its hiding place instead.</summary>
    public string Path { get; set; } = string.Empty;
    public string? DisplayName { get; set; }

    /// <summary>Non-null only while this item is a relocated real desktop file - the original
    /// Desktop/Public Desktop location to move it back to once it's no longer in any fence, or
    /// when Fence Tool exits cleanly. Null for anything dragged in from elsewhere, which never had
    /// a real desktop icon to hide in the first place. See DesktopIconHider.</summary>
    public string? RealDesktopPath { get; set; }

    /// <summary>True only for the single synthetic Recycle Bin item a fence can hold (see
    /// FenceManager.AddRecycleBin) - not backed by a real file, so DesktopIconHider/AddFiles'
    /// existence checks must never run against it, and dropping other items onto it deletes them
    /// instead of the usual add/reorder/move behavior. Path is set to the Recycle Bin's own shell
    /// namespace CLSID string purely so the existing icon-extraction code can render its (empty/
    /// full-aware) system icon unmodified - it's never treated as a filesystem path anywhere else.</summary>
    public bool IsRecycleBin { get; set; }
}

/// <summary>Reads both the current fences.json format (Files as an array of FenceItem objects)
/// and the older pre-rename format (Files as a plain array of path strings), so upgrading doesn't
/// silently wipe out fences saved by an earlier version - see FenceStore.Load, which discards the
/// whole file on any deserialization failure. Always writes the current object-array format.</summary>
internal sealed class FenceItemListConverter : JsonConverter<List<FenceItem>>
{
    public override List<FenceItem> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException();

        var result = new List<FenceItem>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
                result.Add(new FenceItem { Path = reader.GetString() ?? string.Empty });
            else
                result.Add(JsonSerializer.Deserialize<FenceItem>(ref reader, options) ?? new FenceItem());
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<FenceItem> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

/// <summary>Inherits WidgetStyleModel (TintColor/HeaderDarkness/Opacity/FullOpacityOnHover/
/// TintStrength/Margin/CornerRadius/TitleFontSize/TitleAlignment/HeaderBorderMode/LightBorder/
/// HideHeader/HeaderCloseButton) purely so StyleMenuRows/StyleTint's shared helpers can operate
/// against a Fence the
/// same way they do LayoutLauncherModel/WidgetManagerModel - zero effect on JSON serialization
/// (System.Text.Json serializes every inherited public property the same as a directly-declared
/// one) and zero behavior change for FenceForm, which still reads/writes these properties directly,
/// not through the interface.</summary>
public sealed class FenceModel : WidgetStyleModel
{
    // 22 matches the fixed corner radius a fence used before Corner Radius became adjustable -
    // every other widget on this base defaults to WidgetStyleModel's own 10 instead.
    public FenceModel() : base(defaultCornerRadius: 22)
    {
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Fence";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 240;
    public int Height { get; set; } = 200;

    [JsonConverter(typeof(FenceItemListConverter))]
    public List<FenceItem> Files { get; set; } = new();
    public bool Collapsed { get; set; }
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
