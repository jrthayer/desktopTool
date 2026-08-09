using DesktopTool.Features.Fences;
using DesktopTool.Features.Fences.UI;
using DesktopTool.Native;

namespace DesktopTool.UI;

/// <summary>
/// "Copy Settings To" - a real widget built on this same LayeredWidgetForm base every other widget
/// in this app is (a Fence, the Layout Launcher, Widget Manager), rather than a plain WinForms Form
/// bolted together separately - so it gets move/resize/rename/its own Settings menu/persisted
/// position for free, the same way they all do. Its "content" is just three fixed boxes (see Boxes),
/// painted through the base's own generic list mechanism (GetListArea/ListRowCount/PaintListRow) the
/// same way Widget Manager's own fixed three-row list is. Clicking a box applies the source widget's
/// settings to every live widget (see LayeredWidgetForm.LiveWidgets) matching that category.
///
/// Seeded from the source widget's own current look every time a pick starts (see
/// LayeredWidgetForm.FireArmedCopySettingsButton, which calls CopySettingsFrom right after
/// construction) - so it reads as "belonging to" whatever opened it, the same copy every individual
/// click-a-widget target already gets. Being a real, independently-styled widget now, a Settings
/// change made on THIS widget specifically will of course drift from the source's own from then on,
/// same as if you Copy-Settings'd any other widget and then changed its color afterward.
///
/// Shown alongside CopySettingsOverlay's own full-screen picker (see its own call site) - the two
/// close together no matter which one a cancel (Escape/right-click/its own "x") actually lands on.
/// </summary>
internal sealed class CopySettingsGroupPicker : LayeredWidgetForm
{
    private readonly record struct Box(string Label, Func<LayeredWidgetForm, bool> Matches);

    private static readonly Box[] Boxes =
    {
        new("All Widgets", _ => true),
        new("All Fences", w => w is FenceForm),
        new("All Non-Fence Widgets", w => w is not FenceForm),
    };

    // Same values as WidgetManagerWidget's own (OuterMarginPx/ButtonBandOverhang) - no reason for
    // this widget's own margin band to be sized any differently from every other chrome-only widget.
    private const int OuterMarginPx = 13;
    private const int HeaderHeight = 28;
    private const int ButtonBandOverhang = 19;
    private const int TopMarginWithButtons = OuterMarginPx + ButtonBandOverhang;

    private const int BoxRowHeight = 40;
    private const int ListVerticalPadding = 10;
    private const int ListHorizontalPadding = 14;

    // Seeds CreateParams/GetCurrentBody before the widget has ever been resized (see
    // CopySettingsPickerModel.Height's own "null until moved/resized once" comment) - Boxes.Length
    // is fixed, so this is the exact height that fits all three rows plus padding, not a guess.
    private static int DefaultBodyHeight => HeaderHeight + ListVerticalPadding * 2 + Boxes.Length * BoxRowHeight;

    private readonly LayeredWidgetForm _source;
    private readonly CopySettingsPickerModel _model;
    private readonly CopySettingsPickerStore _store;

    private bool _settingsButtonArmed;
    private int _armedRowIndex = -1;
    private int _hoverRowIndex = -1;

    protected override int OuterMargin => OuterMarginPx;
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMarginWithButtons;
    protected override int BottomBand => ButtonRowAtBottom ? TopMarginWithButtons : OuterMargin;
    protected override int MaxTopBand => TopMarginWithButtons;

    protected override IWidgetStyle Style => _model;
    protected override IReadOnlyList<ChromeButton> ExtraButtons { get; }

    public CopySettingsGroupPicker(LayeredWidgetForm source, FenceManager fences, CopySettingsPickerModel model, CopySettingsPickerStore store)
        : base(model.Opacity / 100f, fences)
    {
        _source = source;
        _model = model;
        _store = store;

        // "x" closes outright (Dispose, via the FormClosed wiring at the call site) rather than
        // hiding the way WidgetManagerWidget/LayoutLauncherWidget's own "x" does - there's no
        // persisted-hidden state worth keeping for a widget that's recreated fresh on every pick.
        ExtraButtons = new List<ChromeButton> { new("×", 22, Close) };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // CopySettingsOverlay (see its own class comment) is a full-virtual-screen, TopMost click-
        // catcher shown alongside this widget - without this, it sits ABOVE an ordinary window in
        // z-order and swallows every click meant for one of this widget's own boxes below it.
        TopMost = true;
        KeyPreview = true;
        Font = AppTheme.Font;

        RenderAndPresent();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;

            // Control's base constructor probes CreateParams before our own constructor body has
            // run (so _model is still null at that point) - the real, model-driven CreateParams
            // request comes later, once the constructor body first touches Handle (see
            // WidgetManagerWidget's own identical comment).
            if (_model is null)
                return cp;

            var body = GetCurrentBody();
            ButtonRowAtBottom = ComputeButtonRowAtBottom(body.Location, TopMarginWithButtons);

            cp.Width = body.Width + OuterMargin * 2;
            cp.Height = body.Height + TopBand + BottomBand;
            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_CLIPCHILDREN;
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
            cp.X = body.X - OuterMargin;
            cp.Y = body.Y - TopBand;
            return cp;
        }
    }

    /// <summary>Centered on the source widget's own monitor the very first time this ever opens (no
    /// persisted X/Y yet) - not always the primary one, so a pick started on a fence sitting on a
    /// second display doesn't pop this up in the middle of the first one instead. Every later open
    /// reuses wherever it was last dragged to, same as any other widget's remembered position.</summary>
    protected override Rectangle GetCurrentBody()
    {
        var workingArea = Screen.FromControl(_source).WorkingArea;
        var width = _model.Width;
        var height = _model.Height ?? DefaultBodyHeight;
        return new Rectangle(
            _model.X ?? workingArea.X + (workingArea.Width - width) / 2,
            _model.Y ?? workingArea.Y + (workingArea.Height - height) / 2,
            width,
            height);
    }

    protected override int SnapMargin => _model.Margin;

    // ComputeMovedBody/ComputeResizedBody/SupportsResize/ResizableEdges all use LayeredWidgetForm's
    // own defaults unchanged - full, unrestricted resize on every edge, snapping against every other
    // live widget's edges and custom snap lines the same as any other widget on this base.

    protected override void OnDragEnd()
    {
        if (NativeMethods.GetWindowRect(Handle, out var rect))
        {
            _model.X = rect.Left + OuterMargin;
            _model.Y = rect.Top + TopBand;
            _model.Width = rect.Right - rect.Left - OuterMargin * 2;
            _model.Height = rect.Bottom - rect.Top - TopBand - BottomBand;
            Persist();
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
            || GetCopySettingsButtonRect(contentWidth, onLeft).Contains(contentPoint)
            || TryGetExtraButtonAt(contentWidth, onLeft, contentPoint, out _)))
            return HTCLIENT;

        // Not gated by ShowsButtons, unlike every check above - see IsOverHeaderCloseButton's own
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
        if (ShowsButtons && TryArmCopySettingsButton(contentPoint))
            return;
        if (ShowsButtons && TryArmExtraButton(contentPoint))
            return;
        if (TryHandleListMouseDown(contentPoint))
            return;

        if (TryGetRowIndexAt(contentPoint, out var index))
            _armedRowIndex = index;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateListScrollDrag(ToContent(e.Location));

        var hovered = TryGetRowIndexAt(ToContent(e.Location), out var index) ? index : -1;
        if (hovered == _hoverRowIndex)
            return;
        _hoverRowIndex = hovered;
        RenderAndPresent();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverRowIndex == -1)
            return;
        _hoverRowIndex = -1;
        RenderAndPresent();
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

        FireArmedCopySettingsButton(contentPoint);
        FireArmedExtraButton(contentPoint);
        EndListScrollDrag();

        if (_armedRowIndex < 0)
            return;
        var armedIndex = _armedRowIndex;
        _armedRowIndex = -1;
        if (TryGetRowIndexAt(contentPoint, out var index) && index == armedIndex)
            ApplyBox(armedIndex);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Close();
    }

    /// <summary>Deliberately stays open afterward - lets another box (or another individual widget,
    /// via CopySettingsOverlay underneath) get the same source's settings right after, without
    /// reopening the pick. Skips both the source (copying onto itself is meaningless) and this
    /// picker itself (it already got the exact same copy up front - see the class comment - so
    /// reapplying it here would be a no-op at best).</summary>
    private void ApplyBox(int index)
    {
        var box = Boxes[index];
        foreach (var widget in LiveWidgets)
        {
            if (!ReferenceEquals(widget, _source) && !ReferenceEquals(widget, this) && box.Matches(widget))
                widget.CopySettingsFrom(_source);
        }
    }

    private bool TryGetRowIndexAt(Point contentPoint, out int index)
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

    protected override Rectangle GetListArea(int contentWidth, int contentHeight)
    {
        var top = (_model.HideHeader ? 0 : HeaderHeight) + ListVerticalPadding;
        var height = Math.Max(ListRowHeight, contentHeight - top - ListVerticalPadding);
        return new Rectangle(ListHorizontalPadding, top, contentWidth - ListHorizontalPadding * 2, height);
    }

    protected override int ListRowCount => Boxes.Length;
    protected override int ListRowHeight => BoxRowHeight;

    /// <summary>One box - a small vertical gap on top/bottom of the row's own full height (see
    /// BoxRowHeight) reads as a discrete button rather than a flush, undivided list.</summary>
    protected override void PaintListRow(Graphics g, int index, Rectangle rowRect)
    {
        var boxRect = Rectangle.Inflate(rowRect, 0, -4);

        using (var path = RoundedRectPath.Full(boxRect, 6))
        using (var fill = new SolidBrush(ThemedField))
            g.FillPath(fill, path);

        if (index == _hoverRowIndex)
            PaintButtonHoverTint(g, boxRect);

        using var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var textBrush = new SolidBrush(Color.WhiteSmoke);
        g.DrawString(Boxes[index].Label, Font, textBrush, boxRect, textFormat);
    }

    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight) => PaintChrome(g, contentWidth, contentHeight);

    protected override string Title
    {
        get => _model.Title;
        set { _model.Title = value; Persist(); }
    }

    protected override int TitleRowHeight => HeaderHeight;

    protected override bool HideHeader
    {
        get => _model.HideHeader;
        set { _model.HideHeader = value; Persist(); RenderAndPresent(); }
    }

    protected override bool ShowHeaderCloseButton
    {
        get => _model.HeaderCloseButton;
        set { _model.HeaderCloseButton = value; Persist(); RenderAndPresent(); }
    }

    protected override void PersistStyle() => Persist();

    private void Persist() => _store.Save(_model);

    protected override void DisposeOwnedResources()
    {
        // Nothing owned - no icon cache, no drag ghost, no hand-painted tooltip (every box's own
        // label is plain visible text, unlike the base's icon-only Copy Settings button).
    }
}
