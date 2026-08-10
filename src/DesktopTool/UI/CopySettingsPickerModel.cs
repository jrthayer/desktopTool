namespace DesktopTool.UI;

/// <summary>Persisted state for the "Copy Settings To" group picker (see CopySettingsGroupPicker) -
/// same nullable-until-first-move X/Y shape as WidgetManagerModel/LayoutLauncherModel, since this
/// widget also starts centered (on the source widget's own monitor, rather than always the primary
/// one - see CopySettingsGroupPicker.GetCurrentBody) instead of at a fixed remembered spot the very
/// first time it's ever opened. No persisted Visible, unlike those two - this widget is created and
/// disposed fresh per pick (see LayeredWidgetForm.OpenCopySettingsPicker) rather than kept
/// around hidden between uses.</summary>
public sealed class CopySettingsPickerModel : WidgetStyleModel
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 228;
    public int? Height { get; set; }

    public string Title { get; set; } = "Copy Settings To";
}
