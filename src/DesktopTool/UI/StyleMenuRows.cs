namespace DesktopTool.UI;

/// <summary>The Fence-style settings-dropdown block shared by anything styled via IWidgetStyle -
/// the color grid (Default + 8 presets + Custom... + Eyedropper, see BuildColorGrid) and the
/// Header Darkness/Opacity/Tint Strength sliders plus the Corner Radius/Margin steppers, in that
/// order. A caller
/// (FenceForm, LayoutLauncherWidget, or a future widget) builds its own full row list by
/// prepending/appending whatever extra rows are specific to it (Hide Header, OCD Sizing, etc.)
/// around what Build (or, for a caller with its own differently-shaped slider/margin block,
/// BuildColorGrid alone) returns here, instead of re-typing this same block - and re-risking a
/// subtly different copy - every time.
///
/// Command ids for the color rows are supplied by the caller (colorDefaultId/colorCustomId/
/// colorEyedropId/colorPresetBaseId) rather than fixed constants here, so this can slot into an
/// existing command-id scheme (like FenceForm's own Cmd* consts) without renumbering anything or
/// risking a collision with that caller's other rows.</summary>
internal static class StyleMenuRows
{
    /// <summary>The color grid alone (Default + 8 presets + Custom... + Eyedropper) - split out from
    /// Build below so a caller with its own separately-built sliders/margin block (FenceForm, whose
    /// "Fence Opacity"/"Fence Margin" wording and OCD-sizing rows are interleaved with those in a
    /// way Build's own fixed shape doesn't accommodate) can still share just this part instead of
    /// keeping a second, parallel copy of the same grid. headerText lets each caller keep its own
    /// wording ("Color" here, "Fence Color" for FenceForm) over an otherwise identical row list.</summary>
    public static List<DropdownMenu.Row> BuildColorGrid(
        IWidgetStyle style,
        Color defaultSwatch,
        int colorDefaultId,
        int colorCustomId,
        int colorEyedropId,
        int colorPresetBaseId,
        string headerText = "Color")
    {
        var rows = new List<DropdownMenu.Row>
        {
            new(0, headerText, IsHeader: true),
            new(colorDefaultId, string.Empty, IsGridItem: true, Swatch: defaultSwatch,
                IsChecked: () => style.TintColor is null, Tooltip: "Default"),
        };
        for (var i = 0; i < StyleTint.Presets.Length; i++)
        {
            var presetArgb = StyleTint.Presets[i].ToArgb();
            rows.Add(new DropdownMenu.Row(colorPresetBaseId + i, string.Empty, IsGridItem: true, Swatch: StyleTint.Presets[i],
                IsChecked: () => style.TintColor == presetArgb, Tooltip: StyleTint.PresetNames[i]));
        }
        // Swatch left null - an empty (outline-only) circle, distinct from every real color, rather
        // than a text row - see DropdownMenu.DrawGridItem.
        rows.Add(new DropdownMenu.Row(colorCustomId, string.Empty, IsGridItem: true,
            Glyph: DropdownMenu.GridGlyph.Plus, Tooltip: "Custom..."));
        rows.Add(new DropdownMenu.Row(colorEyedropId, string.Empty, IsGridItem: true,
            Glyph: DropdownMenu.GridGlyph.Eyedropper, Tooltip: "Eyedropper"));
        return rows;
    }

    public static List<DropdownMenu.Row> Build(
        IWidgetStyle style,
        Color defaultSwatch,
        int colorDefaultId,
        int colorCustomId,
        int colorEyedropId,
        int colorPresetBaseId,
        Action<int> onHeaderDarknessChange,
        Action<int> onOpacityChange,
        Action<int> onTintStrengthChange,
        Action<int> onCornerRadiusChange,
        Action<int> onMarginChange,
        int cornerRadiusMax = 50)
    {
        var rows = BuildColorGrid(style, defaultSwatch, colorDefaultId, colorCustomId, colorEyedropId, colorPresetBaseId);

        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        rows.Add(new DropdownMenu.Row(0, "Header Darkness", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSlider: true,
            SliderValue: () => style.HeaderDarkness / 100.0,
            OnSliderChange: value => onHeaderDarknessChange((int)Math.Round(value * 100))));
        rows.Add(new DropdownMenu.Row(0, "Opacity", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSlider: true,
            SliderValue: () => style.Opacity / 100.0,
            OnSliderChange: value => onOpacityChange((int)Math.Round(value * 100))));
        rows.Add(new DropdownMenu.Row(0, "Tint Strength", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSlider: true,
            SliderValue: () => style.TintStrength / 100.0,
            OnSliderChange: value => onTintStrengthChange((int)Math.Round(value * 100))));

        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        rows.Add(new DropdownMenu.Row(0, "Corner Radius", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsStepper: true,
            StepperValue: () => style.CornerRadius, OnStepperChange: onCornerRadiusChange,
            StepperMin: 0, StepperMax: cornerRadiusMax, StepperStep: 1, StepperSuffix: "px"));

        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        rows.Add(new DropdownMenu.Row(0, "Margin", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsStepper: true,
            StepperValue: () => style.Margin, OnStepperChange: onMarginChange,
            StepperMin: 0, StepperMax: 100, StepperStep: 5, StepperSuffix: "px"));

        return rows;
    }

    /// <summary>Handles whichever of the four color-row command ids BuildColorGrid produced above -
    /// returns false for anything else so a caller's own HandleCommand switch can fall through to
    /// its own cases unchanged. currentTint seeds the ColorDialog with the element's current pick
    /// (falling back to defaultSwatch, its own "never picked one yet" color, rather than a fixed
    /// black that might not match) the same way both FenceForm.PickCustomColor and
    /// LayoutLauncherWidget.PickCustomColor already did before this replaced their private copies.
    /// setExactColor is a separate callback from setColor (rather than one setColor(Color?, bool)
    /// signature) since a caller's Eyedropper handling always does more than just set the tint -
    /// see FenceForm.PickEyedropperColor/LayoutLauncherWidget's own equivalent, both of which also
    /// reset Opacity to 100 and Tint Strength to 0 so a fresh pick starts out pixel-exact.</summary>
    public static bool TryHandleColorCommand(int id, int colorDefaultId, int colorCustomId, int colorEyedropId, int colorPresetBaseId,
        Color defaultSwatch, IWin32Window owner, Color? currentTint, Action<Color?> setColor, Action<Color> setExactColor)
    {
        if (id == colorDefaultId)
        {
            setColor(null);
            return true;
        }

        if (id == colorCustomId)
        {
            using var dialog = new ColorDialog { Color = currentTint ?? defaultSwatch, FullOpen = true };
            if (dialog.ShowDialog(owner) == DialogResult.OK)
                setColor(dialog.Color);
            return true;
        }

        if (id == colorEyedropId)
        {
            EyedropperOverlay.Pick(setExactColor);
            return true;
        }

        if (id >= colorPresetBaseId && id < colorPresetBaseId + 100)
        {
            setColor(StyleTint.GetPreset(id - colorPresetBaseId));
            return true;
        }

        return false;
    }

    /// <summary>Same overflow check as FenceForm.ShouldSettingsButtonOpenLeft: measures the actual
    /// menu (plus the widest row tooltip, which reaches further right than the menu's own edge once
    /// hovered) against the screen buttonScreenRectIfRight is on, using the button's default
    /// top-right placement as the anchor - "would the menu fit opening rightward from there".</summary>
    public static bool ShouldOpenLeft(Rectangle buttonScreenRectIfRight, IEnumerable<DropdownMenu.Row> rows, Font font)
    {
        var rowList = rows as IReadOnlyList<DropdownMenu.Row> ?? rows.ToList();
        var workingArea = Screen.FromRectangle(buttonScreenRectIfRight).WorkingArea;
        var menuSize = DropdownMenu.Measure(rowList, font);
        var maxTooltipWidth = DropdownMenu.MaxTooltipWidth(rowList, font);
        var tooltipReach = maxTooltipWidth > 0 ? DropdownMenu.AnchorGap + maxTooltipWidth : 0;
        return buttonScreenRectIfRight.Right + DropdownMenu.AnchorGap + menuSize.Width + tooltipReach > workingArea.Right;
    }
}
