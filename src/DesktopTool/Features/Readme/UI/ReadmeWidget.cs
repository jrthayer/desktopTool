using System.Drawing.Text;
using DesktopTool.Features.Fences;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.Readme.UI;

/// <summary>In-memory-only style/position model for ReadmeWidget - no Store/persistence file of its
/// own (see ReadmeWidget's own class comment for why), just enough state (WidgetStyleModel's own
/// IWidgetStyle knobs plus position/size/title) for LayeredWidgetForm's base chrome to have
/// something to read from. Always starts back at these same defaults - nothing here survives
/// between opens.</summary>
internal sealed class ReadmeModel : WidgetStyleModel
{
    // Full opacity, not WidgetStyleModel.DefaultOpacity (85%, every other widget's own default) -
    // that 15% blend against the desktop behind it is barely visible on a short row label, but
    // compounds with GDI+'s own AntiAlias (grayscale, non-ClearType - see PaintDetailPane) text
    // rendering to make a full paragraph of small body text visibly soft/blurry. A reference window
    // meant to actually be read has no reason to sit translucent the way an ambient Fence does.
    //
    // HeaderCloseButton on by default too, unlike every other widget on this base (off by default -
    // see WidgetStyleModel's own doc comment) - this widget is opened, read, and dismissed in one
    // sitting rather than left running, so it needs a close action reachable without first
    // right-clicking or clicking the title to engage it the way a persistent Fence's own Settings
    // menu can wait for.
    public ReadmeModel()
    {
        Opacity = 100;
        HeaderCloseButton = true;
    }

    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 480;
    public int? Height { get; set; }
    public string Title { get; set; } = "Readme";
}

/// <summary>Generic master/detail reference widget, not a live control surface: a fixed-width list of
/// titles on the left, the selected one's own body text word-wrapped in the pane to the right, instead
/// of one long scroll of text. Originally just "Readme" (opened via Widget Manager's own "?" button -
/// see WidgetManagerWidget.HelpRequested/TrayApplicationContext.OpenReadme - with its own hardcoded
/// Fences/Layout Launcher/Snap Lines/Widget Snapping/Fence Trash Can entries, still the parameterless
/// constructor's default via DefaultEntries below), now reusable for any other (title, entries) pair
/// with the identical list/detail mechanics - see ClaudePipelineWidget's own feature-info window for
/// the second use. Built on LayeredWidgetForm like every other on-screen widget, so it drags/resizes/
/// themes/closes the exact same way rather than being a plain modal dialog - but genuinely ephemeral,
/// unlike Fences/Layout Launcher/Widget Manager: created fresh each time, fully disposed on close (its
/// own header close button - see ReadmeModel's own HeaderCloseButton default - is a real Close(), not
/// this base's usual "cancel and hide" pattern - hence no OnFormClosing override here at all), and
/// backed by ReadmeModel above rather than a saved-to-disk Store, since there's nothing here worth
/// remembering between opens. Entries can change after construction (RefreshEntries) for a caller
/// whose own source data can change while this window happens to be open - the original Readme content
/// never needs that, but nothing stops it from calling RefreshEntries too.</summary>
internal sealed class ReadmeWidget : LayeredWidgetForm
{
    private const int OuterMarginPx = 13;
    private const int HeaderHeight = 28;
    // Same reasoning/value as WidgetManagerWidget's own ButtonBandOverhang - see its own comment.
    private const int ButtonBandOverhang = 19;
    private const int TopMarginWithButtons = OuterMarginPx + ButtonBandOverhang;

    private const int ListVerticalPadding = 8;
    private const int ListHorizontalPadding = 10;
    private const int ListRowHeightConst = 26;
    private const int ListColumnWidth = 150;
    private const int DetailGap = 16;
    private const int DetailTitleHeight = 22;

    // Not derived from row/content measurement the way WidgetManagerWidget.DefaultBodyHeight is -
    // the detail pane's own wrapped-text height doesn't reduce to a clean formula the way a fixed
    // row count does, so this is just a reasonable starting size instead. Resizable afterward like
    // any other widget on this base.
    private const int DefaultContentWidth = 480;
    private const int DefaultContentHeight = 280;

    // Same order as WidgetManagerWidget.Rows - this list exists to be read alongside that one. Only
    // ever used by the parameterless constructor's default - see DefaultEntries below.
    private static readonly (string Title, string Body)[] DefaultEntries =
    {
        new("Fences",
            "Draggable, resizable containers for your desktop icons - drag files onto one to fence " +
            "them instead of leaving them scattered on the desktop. Widget Manager's own switch " +
            "shows or hides every fence at once; its + button adds a new, empty fence."),
        new("Layout Launcher",
            "Saved sets of programs and windows, each pinned to a monitor and screen position, " +
            "launched together with one click. Widget Manager's own switch shows or hides the " +
            "Layout Launcher widget itself; its cog opens Manage Layouts to create, edit, and run " +
            "layouts."),
        new("Snap Lines",
            "Custom guide lines you place anywhere on screen that fences and widgets snap to while " +
            "you're dragging them. Widget Manager's own switch turns your custom lines on or off " +
            "app-wide; its cog opens the snap line editor to add, move, or delete them."),
        new("Widget Snapping",
            "Snapping to every other fence's and widget's own edges while you drag or resize - " +
            "separate from Snap Lines above, and on by default. Widget Manager's own switch turns " +
            "that edge-to-edge snapping on or off app-wide."),
        new("Fence Trash Can",
            "A small, dedicated fence holding just the Recycle Bin icon, sized to wrap tightly " +
            "around it rather than sitting in an ordinary-sized fence. Widget Manager's own switch " +
            "adds or removes it; while it exists, it hides the real desktop's own Recycle Bin icon " +
            "so it isn't shown twice."),
    };

    private readonly ReadmeModel _model;
    private readonly Font _detailTitleFont;
    // One point larger than AppTheme.Font - AA softness is a roughly fixed pixel width at the glyph
    // edge, so it reads proportionally less blurry on a full paragraph of body text at a slightly
    // bigger size, without touching the list rows' own single-line Font.
    private readonly Font _detailBodyFont;
    private IReadOnlyList<(string Title, string Body)> _entries;

    private bool _settingsButtonArmed;
    private int? _armedRowIndex;
    private int _selectedIndex;

    protected override int OuterMargin => OuterMarginPx;
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMarginWithButtons;
    protected override int BottomBand => ButtonRowAtBottom ? TopMarginWithButtons : OuterMargin;
    protected override int MaxTopBand => TopMarginWithButtons;

    protected override IWidgetStyle Style => _model;

    /// <summary>The original Readme, unchanged - DefaultEntries/"Readme" via the parameterized
    /// constructor below.</summary>
    public ReadmeWidget(FenceManager fenceManager) : this(fenceManager, "Readme", DefaultEntries, 0) { }

    // 1f (full opacity), matching ReadmeModel's own Opacity default - not read from _model itself,
    // which can't be read yet this early (see CreateParams' own "Control's base constructor probes
    // CreateParams before our own constructor body has run" comment, the same timing issue here for
    // the base constructor's own opacity argument), but a fresh ReadmeModel always starts at exactly
    // this value anyway, so there's nothing lost by writing the literal directly instead.
    /// <summary>title/entries/selectedIndex are what make this reusable for something other than the
    /// original Readme content (see this class's own comment) - selectedIndex is clamped against
    /// entries so an out-of-range value (or an empty list) can never crash construction.</summary>
    public ReadmeWidget(FenceManager fenceManager, string title, IReadOnlyList<(string Title, string Body)> entries, int selectedIndex)
        : base(1f, fenceManager)
    {
        _model = new ReadmeModel { Title = title };
        _entries = entries;
        _selectedIndex = entries.Count == 0 ? 0 : Math.Clamp(selectedIndex, 0, entries.Count - 1);
        _detailTitleFont = new Font(AppTheme.Font, FontStyle.Bold);
        _detailBodyFont = new Font(AppTheme.Font.FontFamily, AppTheme.Font.Size + 1f);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = AppTheme.Font;

        // Forces handle creation now that every field CreateParams needs is set.
        RenderAndPresent();
    }

    /// <summary>Lets a caller whose own source data can change while this window is open (a
    /// ClaudePipelineManager.FeaturesChanged, say) push the new list in - a no-op for the original
    /// Readme content, which never changes. Re-clamps the current selection instead of resetting it
    /// to 0, so editing/toggling some other entry doesn't bounce the view away from the one still
    /// being read.</summary>
    public void RefreshEntries(IReadOnlyList<(string Title, string Body)> entries)
    {
        _entries = entries;
        if (_selectedIndex >= _entries.Count)
            _selectedIndex = Math.Max(0, _entries.Count - 1);
        RenderAndPresent();
    }

    /// <summary>Jumps an already-open window to a specific entry - same "activate an existing instance
    /// instead of opening a second copy" idea as LayoutEditorForm.SelectProfileById/ClaudePipelineEditorForm.
    /// SelectFeatureById, just by index (this class has no notion of a stable Id of its own) rather than
    /// a Guid.</summary>
    public void SelectEntry(int index)
    {
        if (index < 0 || index >= _entries.Count || index == _selectedIndex)
            return;
        _selectedIndex = index;
        RenderAndPresent();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;

            // Same guard, same reason, as WidgetManagerWidget's own CreateParams - Control's base
            // constructor probes this before _model is assigned.
            if (_model is null)
                return cp;

            var bodyX = _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2;
            var bodyY = _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - DefaultContentHeight) / 2;
            var bodyHeight = _model.Height ?? DefaultContentHeight;

            ButtonRowAtBottom = ComputeButtonRowAtBottom(new Point(bodyX, bodyY), TopMarginWithButtons);

            cp.Width = _model.Width + OuterMargin * 2;
            cp.Height = bodyHeight + TopBand + BottomBand;
            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_CLIPCHILDREN;
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
            cp.X = bodyX - OuterMargin;
            cp.Y = bodyY - TopBand;
            return cp;
        }
    }

    protected override Rectangle GetCurrentBody() => new(
        _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2,
        _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - DefaultContentHeight) / 2,
        _model.Width,
        _model.Height ?? DefaultContentHeight);

    protected override int SnapMargin => _model.Margin;

    protected override void OnDragEnd()
    {
        if (NativeMethods.GetWindowRect(Handle, out var rect))
        {
            _model.X = rect.Left + OuterMargin;
            _model.Y = rect.Top + TopBand;
            _model.Width = rect.Right - rect.Left - OuterMargin * 2;
            _model.Height = rect.Bottom - rect.Top - TopBand - BottomBand;
        }

        RenderOpacity.BeginIfNeeded();
    }

    protected override int HitTest(IntPtr lParam)
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
            return HTCLIENT;

        var windowPoint = ScreenLParamToWindowPoint(lParam, rect);
        int x = windowPoint.X;
        int y = windowPoint.Y;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        var contentWidth = width - OuterMargin * 2;
        var contentPoint = ToContent(windowPoint);
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        if (ShowsButtons && (GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint)
            || TryGetExtraButtonAt(contentWidth, onLeft, contentPoint, out _)))
            return HTCLIENT;

        // Not gated by ShowsButtons, unlike the check above - see IsOverHeaderCloseButton's own
        // comment.
        if (IsOverHeaderCloseButton(contentPoint))
            return HTCLIENT;

        if (ShowsButtons)
        {
            var band = OuterMargin + ResizeMargin;
            var topZone = TopBand + ResizeMargin;
            var bottomZone = BottomBand + ResizeMargin;
            if (x <= band || x >= width - band || y <= topZone || y >= height - bottomZone)
                return HTCAPTION;
        }
        else if (ResizeHitTest(windowPoint, width, height) is int resizeCode)
        {
            return resizeCode;
        }

        if (!_model.HideHeader && y - TopBand <= HeaderHeight)
            return HTBORDER;

        return HTCLIENT;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentWidth = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);

        if (TryArmHeaderCloseButton(contentPoint))
            return;
        if (ShowsButtons && GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint))
        {
            _settingsButtonArmed = true;
            return;
        }
        if (ShowsButtons && TryArmExtraButton(contentPoint))
            return;
        if (TryHandleListMouseDown(contentPoint))
            return;

        if (TryGetRowAt(contentPoint, out var index))
            _armedRowIndex = index;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateListScrollDrag(ToContent(e.Location));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        HandleListMouseWheel(e.Delta);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentWidth = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);

        FireArmedHeaderCloseButton(contentPoint);

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            if (ShowsButtons && GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint))
                OpenSettingsMenu();
            return;
        }

        FireArmedExtraButton(contentPoint);
        EndListScrollDrag();

        if (_armedRowIndex is int armedIndex)
        {
            _armedRowIndex = null;
            if (TryGetRowAt(contentPoint, out var index) && index == armedIndex && index != _selectedIndex)
            {
                _selectedIndex = index;
                RenderAndPresent();
            }
        }
    }

    protected override string Title
    {
        get => _model.Title;
        set => _model.Title = value;
    }

    protected override int TitleRowHeight => HeaderHeight;

    protected override bool HideHeader
    {
        get => _model.HideHeader;
        set
        {
            _model.HideHeader = value;
            RenderAndPresent();
        }
    }

    protected override bool ShowHeaderCloseButton
    {
        get => _model.HeaderCloseButton;
        set
        {
            _model.HeaderCloseButton = value;
            RenderAndPresent();
        }
    }

    // Nothing to flush to disk - see this class's own comment on ReadmeModel above.
    protected override void PersistStyle() { }

    protected override void DisposeOwnedResources()
    {
        _detailTitleFont.Dispose();
        _detailBodyFont.Dispose();
    }

    /// <summary>The row list occupies only the left column, unlike WidgetManagerWidget/
    /// LayoutLauncherWidget's own full-width lists - GetListArea's own width is entirely up to the
    /// subclass, so this just claims a fixed left-hand slice instead, leaving the rest of the content
    /// for GetDetailArea/PaintDetailPane below.</summary>
    protected override Rectangle GetListArea(int contentWidth, int contentHeight)
    {
        var top = (_model.HideHeader ? 0 : HeaderHeight) + ListVerticalPadding;
        var height = Math.Max(ListRowHeight, contentHeight - top - ListVerticalPadding);
        return new Rectangle(ListHorizontalPadding, top, ListColumnWidth, height);
    }

    protected override int ListRowCount => _entries.Count;
    protected override int ListRowHeight => ListRowHeightConst;

    /// <summary>Which row (if any) contentPoint lands on - simpler than WidgetManagerWidget's own
    /// TryGetRowAt: every row here is a single click target (select it), not a switch/button pair, so
    /// there's no per-row sub-rect to test beyond the list area itself.</summary>
    private bool TryGetRowAt(Point contentPoint, out int index)
    {
        index = -1;
        var size = GetContentSize();
        var area = GetListArea(size.Width, size.Height);
        if (area.IsEmpty || !area.Contains(contentPoint))
            return false;

        var candidate = (contentPoint.Y - area.Top + ListScrollOffset) / ListRowHeight;
        if (candidate < 0 || candidate >= ListRowCount)
            return false;

        index = candidate;
        return true;
    }

    /// <summary>The selected row uses ThemedMenuSelected (the same "selected" color the Settings
    /// dropdown's own rows use) instead of WidgetManagerWidget's plain alternating banding, since
    /// selection here is the whole point of the list rather than incidental row striping.</summary>
    protected override void PaintListRow(Graphics g, int index, Rectangle rowRect)
    {
        var rowBackground = index == _selectedIndex
            ? ThemedMenuSelected
            : index % 2 == 0 ? ThemedListRow : ThemedListRowDark;
        using (var rowFill = new SolidBrush(rowBackground))
            g.FillRectangle(rowFill, rowRect);

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var textBrush = new SolidBrush(Color.WhiteSmoke))
        using (var textFormat = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(_entries[index].Title, Font, textBrush, new Rectangle(rowRect.X + 8, rowRect.Y, rowRect.Width - 12, rowRect.Height), textFormat);
        g.TextRenderingHint = previousTextHint;
    }

    /// <summary>Where the selected entry's own title+body paint - everything to the right of the
    /// list, minus the same padding GetListArea gives its own left edge.</summary>
    private Rectangle GetDetailArea(int contentWidth, int contentHeight)
    {
        var listArea = GetListArea(contentWidth, contentHeight);
        var x = listArea.Right + DetailGap;
        var width = Math.Max(0, contentWidth - x - ListHorizontalPadding);
        return new Rectangle(x, listArea.Top, width, listArea.Height);
    }

    /// <summary>Painted after PaintChrome (see PaintContent) - a vertical divider matching the list's
    /// own border color, then the selected Entry's title (bold) and word-wrapped body underneath.
    /// Not part of the generic list mechanism at all (PaintListRow only ever paints inside the list's
    /// own area) - just an ordinary paint call this widget adds on top, the same way WidgetManagerWidget
    /// adds its own row tooltip on top of PaintChrome.</summary>
    private void PaintDetailPane(Graphics g, int contentWidth, int contentHeight)
    {
        var listArea = GetListArea(contentWidth, contentHeight);
        var detailArea = GetDetailArea(contentWidth, contentHeight);
        // Empty entries (a caller's own source list can legitimately have nothing in it right now,
        // unlike the original static Readme content) - nothing to select, nothing to paint here.
        if (detailArea.Width <= 0 || _entries.Count == 0)
            return;

        var dividerX = listArea.Right + DetailGap / 2;
        using (var dividerPen = new Pen(ThemedBorder))
            g.DrawLine(dividerPen, ToWindow(new Point(dividerX, listArea.Top)), ToWindow(new Point(dividerX, listArea.Bottom)));

        var entry = _entries[_selectedIndex];
        var windowRect = ToWindow(detailArea);

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var titleRect = new Rectangle(windowRect.X, windowRect.Y, windowRect.Width, DetailTitleHeight);
        using (var titleBrush = new SolidBrush(Color.WhiteSmoke))
            g.DrawString(entry.Title, _detailTitleFont, titleBrush, titleRect);

        var bodyRect = new Rectangle(windowRect.X, windowRect.Y + DetailTitleHeight, windowRect.Width, windowRect.Height - DetailTitleHeight);
        using (var bodyBrush = new SolidBrush(Color.WhiteSmoke))
            g.DrawString(entry.Body, _detailBodyFont, bodyBrush, bodyRect);

        g.TextRenderingHint = previousTextHint;
    }

    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        PaintChrome(g, contentWidth, contentHeight);
        PaintDetailPane(g, contentWidth, contentHeight);
    }
}
