namespace DesktopTool.UI;

/// <summary>The IWidgetStyle knobs - plus HideHeader/HeaderCloseButton, which aren't part of that
/// interface but were still redeclared identically everywhere IWidgetStyle was - that FenceModel,
/// LayoutLauncherModel, and WidgetManagerModel used to each retype byte-for-byte. Centralized here so
/// a fourth widget wanting the same styling only has to inherit this instead of retyping (and
/// re-risking a subtly different copy of) all twelve properties again - see each property's own doc
/// comment on IWidgetStyle for what it actually does.
///
/// Position/size/title/visibility stay out of this base on purpose - FenceModel's own shape there
/// (non-nullable X/Y/Width/Height with real starting defaults, a Name rather than a Title, no
/// persisted Visible at all - a fence is shown/hidden all together, not per-fence) is genuinely
/// different from LayoutLauncherModel/WidgetManagerModel's (nullable until first moved, a Title, a
/// persisted Visible), not just a coincidentally-different copy of the same thing.</summary>
public abstract class WidgetStyleModel : IWidgetStyle
{
    // Shared with FenceManager.SetTintColor's/each widget's own "click the same color again resets
    // these" gesture (and, via LayeredWidgetForm.ApplyTintPick, the base's own reset-on-plain-pick
    // logic), so the reset target and each property's own initial value can never drift apart.
    public const int DefaultHeaderDarkness = 65;
    public const int DefaultOpacity = 85;
    public const int DefaultTintStrength = 55;

    /// <summary>defaultCornerRadius is the one style value that genuinely differs by widget (a
    /// Fence's 22 vs. every other widget's 10, both matching the fixed radius each used before this
    /// became adjustable) - a constructor parameter rather than a fixed initializer here, so each
    /// subclass still gets its own correct default without needing to hide/re-declare the property
    /// itself.</summary>
    protected WidgetStyleModel(int defaultCornerRadius = 10)
    {
        CornerRadius = defaultCornerRadius;
    }

    public int? TintColor { get; set; }
    public bool TintIsExact { get; set; }
    public int HeaderDarkness { get; set; } = DefaultHeaderDarkness;
    public int Opacity { get; set; } = DefaultOpacity;
    public bool FullOpacityOnHover { get; set; }
    public int TintStrength { get; set; } = DefaultTintStrength;
    public int Margin { get; set; }
    public int CornerRadius { get; set; }
    public int TitleFontSize { get; set; } = 9;
    public TitleAlignment TitleAlignment { get; set; } = TitleAlignment.Left;
    public bool HeaderBorderMode { get; set; }
    public bool LightBorder { get; set; }

    /// <summary>Not part of IWidgetStyle (it's read/written through LayeredWidgetForm.HideHeader
    /// instead, which each subclass wires to this same property) - identical across every widget
    /// model regardless, so it lives here rather than being re-typed three times too. Hides the
    /// entire title row, not just its text - see LayeredWidgetForm.TitleVisible.</summary>
    public bool HideHeader { get; set; }

    /// <summary>Not part of IWidgetStyle either, same reasoning as HideHeader above - read/written
    /// through LayeredWidgetForm.ShowHeaderCloseButton. Off by default for every widget except
    /// ReadmeWidget (see its own constructor) - a persistent widget like a Fence is normally
    /// shown/hidden from Widget Manager rather than closed, so an always-visible close glyph in its
    /// header is opt-in rather than the default.</summary>
    public bool HeaderCloseButton { get; set; }
}
