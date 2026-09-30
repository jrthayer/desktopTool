using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Readme.UI;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.GetShitDone.UI;

/// <summary>
/// "Get Shit Done" widget - the front end for CommitmentLog. Lists today's commitments as rows; an open row
/// carries three close-out buttons (done / partly done / skipped - see GetRowActionAt), a closed row
/// a single outcome glyph that reopens it. "Commit..." and any row's own text open an EditBox over
/// the bottom button row (see OpenInput) for typing a new commitment or a note; "Review" opens the
/// log's own outcome breakdown in a ReadmeWidget, same as ClaudePipelineWidget's feature-info window.
/// The line above the buttons (see StatusText) is where the day's finish line shows up: once
/// everything committed is closed out, it says so.
/// Everything not genuinely specific to this widget (theme derivation, the Settings dropdown's default
/// rows, button/border/title/list painting) is LayeredWidgetForm's own - see its own class comment.
/// </summary>
internal sealed class GetShitDoneWidget : LayeredWidgetForm
{
    private const int OuterMarginPx = 13;
    private const int HeaderHeight = 28;
    private const int ButtonBandOverhang = 19;
    private const int TopMarginWithButtons = OuterMarginPx + ButtonBandOverhang;

    private const int ListVerticalPadding = 10;
    private const int ListHorizontalPadding = 10;
    private const int ListRowHeightConst = 26;
    private const int StatusHeight = 22;
    private const int BottomRowHeight = 26;
    private const int BottomRowGap = 8;
    private const int BottomRowBottomPadding = 12;
    private static readonly int[] BottomRowWidths = { 96, 76 };

    // Exactly fits a full default day (CommitmentLog.DefaultMaxPerDay rows) with the two bottom
    // buttons side by side, which they are at GetShitDoneWidgetModel's own default Width.
    private const int DefaultBodyHeight = HeaderHeight + ListVerticalPadding * 2
        + CommitmentLog.DefaultMaxPerDay * ListRowHeightConst + StatusHeight + BottomRowHeight + BottomRowBottomPadding;

    private const int ReviewDays = 30;

    private readonly CommitmentLog _log;
    private readonly GetShitDoneWidgetModel _model;
    private readonly GetShitDoneWidgetStore _store;

    // Today's rows, refreshed (see RefreshToday) whenever the log or the day itself changes rather
    // than re-queried on every paint/hit-test.
    private IReadOnlyList<Commitment> _today = Array.Empty<Commitment>();
    private DateOnly _shownDay;

    // Picks up the one thing nothing else announces: the day rolling over (yesterday's still-open
    // rows become "no check-in" - see CommitmentLog.SweepStale).
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 30_000 };

    private bool _allowClose;
    private bool _settingsButtonArmed;

    // Same arm-then-fire pattern as every other button on this base - see ClaudePipelineWidget's own
    // RowAction for the precedent.
    private enum RowAction { None, Done, Partial, Skip, Reopen, Note }
    private RowAction _armedRowAction = RowAction.None;
    private int _armedRowIndex = -1;

    // The one text box this widget ever shows, for either a new commitment or a row's note - a
    // second EditBox alongside the base's own rename box, since a layered window has no Controls
    // collection to host a real TextBox in (see EditBox's own class comment).
    private enum InputMode { None, Commit, Note }
    private InputMode _inputMode = InputMode.None;
    private Guid _noteTargetId;
    private EditBox? _inputBox;

    // One-off feedback shown in place of the day's status until the next click.
    private string? _flash;

    private readonly PaintedTooltip _rowTooltip = new();
    private readonly Font _closedRowFont;

    // Same "create once, activate/refresh the existing one" idea as ClaudePipelineWidget's own
    // _featureInfoWidget.
    private ReadmeWidget? _reviewWidget;

    protected override IReadOnlyList<ChromeButton> ExtraButtons { get; }

    /// <summary>Fired whenever ShowAndPersist/HideAndPersist actually change Visible - lets
    /// WidgetManagerWidget repaint its own Get Shit Done row immediately, same reasoning as
    /// LayoutLauncherWidget.VisibilityChanged.</summary>
    public event EventHandler? VisibilityChanged;

    protected override int OuterMargin => OuterMarginPx;
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMarginWithButtons;
    protected override int BottomBand => ButtonRowAtBottom ? TopMarginWithButtons : OuterMargin;
    protected override int MaxTopBand => TopMarginWithButtons;

    protected override IWidgetStyle Style => _model;

    public GetShitDoneWidget(CommitmentLog log, FenceManager fenceManager, GetShitDoneWidgetModel model, GetShitDoneWidgetStore store)
        : base(model.Opacity / 100f, fenceManager)
    {
        _log = log;
        _model = model;
        _store = store;
        _closedRowFont = new Font(AppTheme.Font, FontStyle.Strikeout);

        _log.SweepStale(DateTime.Now);
        RefreshToday();
        _log.Changed += OnLogChanged;
        _tick.Tick += OnTick;
        _tick.Start();

        ExtraButtons = new List<ChromeButton>
        {
            new("×", 22, HideAndPersist, "Hide Get Shit Done"),
        };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = AppTheme.Font;

        RenderAndPresent();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
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

    public void ToggleVisible()
    {
        if (Visible)
            HideAndPersist();
        else
            ShowAndPersist();
    }

    private void ShowAndPersist()
    {
        Show();
        Activate();
        _model.Visible = true;
        Persist();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HideAndPersist()
    {
        Hide();
        _model.Visible = false;
        Persist();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Shutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing
            or CloseReason.ApplicationExitCall or CloseReason.FormOwnerClosing))
        {
            e.Cancel = true;
            HideAndPersist();
            return;
        }

        base.OnFormClosing(e);
    }

    private void Persist() => _store.Save(_model);

    private void RefreshToday()
    {
        _shownDay = CommitmentLog.DayOf(DateTime.Now);
        _today = _log.ForDay(_shownDay);
    }

    private void OnLogChanged(object? sender, EventArgs e)
    {
        RefreshToday();
        RenderAndPresent();

        if (_reviewWidget is { IsDisposed: false })
            _reviewWidget.RefreshEntries(BuildReviewEntries());
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        // SweepStale raises Changed (and so OnLogChanged) itself whenever it closes anything - only
        // a day rollover with nothing left open needs handling here.
        if (_log.SweepStale(now) == 0 && CommitmentLog.DayOf(now) != _shownDay)
            OnLogChanged(this, EventArgs.Empty);
    }

    protected override void DisposeOwnedResources()
    {
        _log.Changed -= OnLogChanged;
        _tick.Dispose();
        _inputBox?.Dispose();
        _reviewWidget?.Dispose();
        _closedRowFont.Dispose();
    }

    private List<(string Title, string Body)> BuildReviewEntries() =>
        Review.Build(_log.All, DateTime.Now, ReviewDays).Select(s => (s.Title, s.Body)).ToList();

    private void OpenReview()
    {
        if (_reviewWidget is { IsDisposed: false })
        {
            _reviewWidget.RefreshEntries(BuildReviewEntries());
            _reviewWidget.Activate();
            return;
        }

        _reviewWidget = new ReadmeWidget(Fences, $"Review - last {ReviewDays} days", BuildReviewEntries(), 0);
        _reviewWidget.FormClosed += (_, _) => _reviewWidget = null;
        _reviewWidget.Show();
    }

    // ---- Text input (new commitment / note) ----

    private void BeginCommit()
    {
        if (_today.Count >= _model.MaxPerDay)
        {
            _flash = $"{_model.MaxPerDay} a day is the cap.";
            RenderAndPresent();
            return;
        }

        OpenInput(InputMode.Commit, string.Empty);
    }

    private void OpenInput(InputMode mode, string initialText)
    {
        if (_inputBox is not null)
            return;

        var size = GetContentSize();
        _inputMode = mode;
        _inputBox = new EditBox(Handle, initialText, ToScreen(ToWindow(GetInputRect(size.Width, size.Height))), Font);
        _inputBox.Commit += OnInputCommit;
        _inputBox.Cancel += CloseInput;
        RenderAndPresent();
    }

    private void OnInputCommit(string text)
    {
        var mode = _inputMode;
        CloseInput();

        if (mode == InputMode.Note)
            _log.SetNote(_noteTargetId, text);
        else if (mode == InputMode.Commit && !string.IsNullOrWhiteSpace(text)
            && _log.Add(text, DateTime.Now, _model.MaxPerDay) is null)
        {
            // Only an estimate/tag and nothing else.
            _flash = "Nothing added.";
            RenderAndPresent();
        }
    }

    private void CloseInput()
    {
        _inputBox?.Dispose();
        _inputBox = null;
        _inputMode = InputMode.None;
        RenderAndPresent();
    }

    /// <summary>Where the input EditBox sits - over the bottom button block, which it covers while
    /// open, leaving the status line above it free to say what's being asked for (see StatusText).</summary>
    private static Rectangle GetInputRect(int contentWidth, int contentHeight)
    {
        var top = contentHeight - BottomRowBottomPadding - BottomRowHeight;
        return new Rectangle(ListHorizontalPadding, top + 2, contentWidth - ListHorizontalPadding * 2, BottomRowHeight - 4);
    }

    private string StatusText()
    {
        switch (_inputMode)
        {
            case InputMode.Commit: return "e.g. draft intro 45m #writing";
            case InputMode.Note: return "What got in the way? Enter saves, Esc cancels";
        }

        if (_flash is not null)
            return _flash;

        var closed = _today.Count(c => c.Outcome != Outcome.Open);
        return _log.StatusOf(_shownDay) switch
        {
            DayStatus.Empty => "Nothing committed yet.",
            DayStatus.InProgress => $"{closed} of {_today.Count} closed out",
            DayStatus.CheckedIn => "Checked in for today.",
            _ => "Done for today. The rest is yours.",
        };
    }

    // ---- Geometry / drag ----

    protected override Rectangle GetCurrentBody() => new(
        _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2,
        _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - DefaultBodyHeight) / 2,
        _model.Width,
        _model.Height ?? DefaultBodyHeight);

    protected override int SnapMargin => _model.Margin;

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
            || TryGetExtraButtonAt(contentWidth, onLeft, contentPoint, out _)))
            return HTCLIENT;

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

    // ---- Mouse ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        _flash = null;

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
        if (TryArmContentButton(contentPoint))
            return;
        if (TryHandleListMouseDown(contentPoint))
            return;

        var (action, index) = GetRowActionAt(contentPoint);
        if (action != RowAction.None)
        {
            _armedRowAction = action;
            _armedRowIndex = index;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateListScrollDrag(ToContent(e.Location));
        UpdateRowTooltips(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_rowTooltip.Hide())
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

        FireArmedExtraButton(contentPoint);
        FireArmedContentButton(contentPoint);
        EndListScrollDrag();

        if (_armedRowAction != RowAction.None)
        {
            var armedAction = _armedRowAction;
            var armedIndex = _armedRowIndex;
            _armedRowAction = RowAction.None;
            _armedRowIndex = -1;

            var (action, index) = GetRowActionAt(contentPoint);
            if (action == armedAction && index == armedIndex)
                FireRowAction(action, index);
        }
    }

    // ---- Base hooks ----

    protected override string Title
    {
        get => _model.Title;
        set
        {
            _model.Title = value;
            Persist();
        }
    }

    protected override int TitleRowHeight => HeaderHeight;

    protected override bool HideHeader
    {
        get => _model.HideHeader;
        set
        {
            _model.HideHeader = value;
            Persist();
            RenderAndPresent();
        }
    }

    protected override bool ShowHeaderCloseButton
    {
        get => _model.HeaderCloseButton;
        set
        {
            _model.HeaderCloseButton = value;
            Persist();
            RenderAndPresent();
        }
    }

    protected override void PersistStyle() => Persist();

    protected override void CopyAdditionalSettingsFrom(LayeredWidgetForm source)
    {
        if (source is GetShitDoneWidget other)
            _model.MaxPerDay = other._model.MaxPerDay;
    }

    private const int MaxPerDayLimit = 5;

    protected override IReadOnlyList<DropdownMenu.Row>? BuildAdditionalSettingsRows() => new List<DropdownMenu.Row>
    {
        new(0, "Commitments Per Day", IsHeader: true),
        new(0, string.Empty, IsStepper: true,
            StepperValue: () => _model.MaxPerDay,
            OnStepperChange: max =>
            {
                _model.MaxPerDay = Math.Clamp(max, 1, MaxPerDayLimit);
                Persist();
            },
            StepperMin: 1, StepperMax: MaxPerDayLimit, StepperStep: 1, StepperSuffix: ""),
    };

    protected override IReadOnlyList<ContentButton> GetContentButtons(int contentWidth, int contentHeight)
    {
        // Covered by the input EditBox while it's open (see GetInputRect).
        if (_inputBox is not null)
            return Array.Empty<ContentButton>();

        var top = contentHeight - BottomRowBottomPadding - RowHeight(contentWidth, BottomRowHeight, BottomRowGap, BottomRowWidths);
        var rects = LayoutRow(contentWidth, top, BottomRowHeight, BottomRowGap, BottomRowWidths);

        return new[]
        {
            new ContentButton("Commit...", rects[0], BeginCommit),
            new ContentButton("Review", rects[1], OpenReview),
        };
    }

    private int BottomBlockHeight(int contentWidth) =>
        StatusHeight + BottomRowBottomPadding + RowHeight(contentWidth, BottomRowHeight, BottomRowGap, BottomRowWidths);

    private Rectangle GetStatusRect(int contentWidth, int contentHeight) =>
        new(ListHorizontalPadding, contentHeight - BottomBlockHeight(contentWidth), contentWidth - ListHorizontalPadding * 2, StatusHeight);

    protected override Rectangle GetListArea(int contentWidth, int contentHeight)
    {
        var top = (_model.HideHeader ? 0 : HeaderHeight) + ListVerticalPadding;
        var bottom = contentHeight - BottomBlockHeight(contentWidth) - ListVerticalPadding;
        return new Rectangle(ListHorizontalPadding, top, contentWidth - ListHorizontalPadding * 2, Math.Max(ListRowHeight, bottom - top));
    }

    protected override int ListRowCount => _today.Count;
    protected override int ListRowHeight => ListRowHeightConst;

    // ---- Rows ----

    private const int RowButtonSize = 20;
    private const int RowButtonGap = 2;
    private const int RowButtonRightPadding = 6;

    /// <summary>Slot 0 is flush against the row's right edge, each higher slot chained left of it -
    /// pure relative math off rowRect's own edges, same convention as ClaudePipelineWidget.
    /// GetRowSwitchRect, so it works for both a content-space (hit-test) and a paint-space rowRect.</summary>
    private static Rectangle GetRowButtonRect(Rectangle rowRect, int slot) => new(
        rowRect.Right - RowButtonRightPadding - RowButtonSize - slot * (RowButtonSize + RowButtonGap),
        rowRect.Y + (rowRect.Height - RowButtonSize) / 2, RowButtonSize, RowButtonSize);

    // An open row's three close-out buttons, left to right: done, partly done, skipped.
    private static readonly (RowAction Action, int Slot)[] OpenRowButtons =
    {
        (RowAction.Done, 2), (RowAction.Partial, 1), (RowAction.Skip, 0),
    };

    private Rectangle GetRowTextArea(Rectangle rowRect, int index)
    {
        var stopX = GetRowButtonRect(rowRect, _today[index].Outcome == Outcome.Open ? 2 : 0).X;
        return new Rectangle(rowRect.X + 8, rowRect.Y, Math.Max(0, stopX - 4 - (rowRect.X + 8)), rowRect.Height);
    }

    private bool TryGetRowAt(Point contentPoint, out int index, out Rectangle rowRect)
    {
        index = -1;
        rowRect = Rectangle.Empty;

        var size = GetContentSize();
        var area = GetListArea(size.Width, size.Height);
        if (area.IsEmpty || !area.Contains(contentPoint))
            return false;

        var candidate = (contentPoint.Y - area.Top + ListScrollOffset) / ListRowHeight;
        if (candidate < 0 || candidate >= ListRowCount)
            return false;

        var maxScroll = Math.Max(0, ListRowCount * ListRowHeight - area.Height);
        var rowWidth = maxScroll > 0 ? area.Width - (Scrollbar.Width + Scrollbar.Margin * 2) : area.Width;
        var rowTop = area.Top + candidate * ListRowHeight - ListScrollOffset;
        var rect = new Rectangle(area.Left, rowTop, rowWidth, ListRowHeight);
        if (!rect.Contains(contentPoint))
            return false;

        index = candidate;
        rowRect = rect;
        return true;
    }

    /// <summary>What clicking contentPoint would do right now, and the rect that action covers (for
    /// its tooltip) - one of an open row's three close-out buttons, a closed row's outcome glyph
    /// (reopens it), or the row's own text (opens its note).</summary>
    private (RowAction Action, int Index, Rectangle TargetRect) GetRowTargetAt(Point contentPoint)
    {
        if (!TryGetRowAt(contentPoint, out var index, out var rowRect))
            return (RowAction.None, -1, Rectangle.Empty);

        if (_today[index].Outcome == Outcome.Open)
        {
            foreach (var (action, slot) in OpenRowButtons)
            {
                var rect = GetRowButtonRect(rowRect, slot);
                if (rect.Contains(contentPoint))
                    return (action, index, rect);
            }
        }
        else if (GetRowButtonRect(rowRect, 0) is var outcomeRect && outcomeRect.Contains(contentPoint))
        {
            return (RowAction.Reopen, index, outcomeRect);
        }

        return (RowAction.Note, index, GetRowTextArea(rowRect, index));
    }

    private (RowAction Action, int Index) GetRowActionAt(Point contentPoint)
    {
        var (action, index, _) = GetRowTargetAt(contentPoint);
        return (action, index);
    }

    private string RowActionTooltipText(RowAction action, int index)
    {
        var commitment = _today[index];
        return action switch
        {
            RowAction.Done => "Done",
            RowAction.Partial => "Partly done",
            RowAction.Skip => "Skipped",
            RowAction.Reopen => $"Reopen (was {Review.Name(commitment.Outcome)})",
            _ => commitment.Note ?? "Add a note",
        };
    }

    private void UpdateRowTooltips(Point windowLocation)
    {
        var (action, index, targetRect) = GetRowTargetAt(ToContent(windowLocation));

        var changed = action != RowAction.None
            ? _rowTooltip.Show(RowActionTooltipText(action, index), ToWindow(targetRect))
            : _rowTooltip.Hide();

        if (changed)
            RenderAndPresent();
    }

    private void FireRowAction(RowAction action, int index)
    {
        if (index < 0 || index >= _today.Count)
            return;
        var commitment = _today[index];
        var now = DateTime.Now;

        switch (action)
        {
            case RowAction.Done:
                _log.Close(commitment.Id, Outcome.Done, now);
                break;
            // Recorded first, then asked about - the note is optional, and dismissing the box (or
            // just clicking away) must never lose the close-out itself.
            case RowAction.Partial:
                _log.Close(commitment.Id, Outcome.Partial, now);
                BeginNote(commitment);
                break;
            case RowAction.Skip:
                _log.Close(commitment.Id, Outcome.Skipped, now);
                BeginNote(commitment);
                break;
            case RowAction.Reopen:
                _log.Reopen(commitment.Id);
                break;
            case RowAction.Note:
                BeginNote(commitment);
                break;
        }
    }

    private void BeginNote(Commitment commitment)
    {
        _noteTargetId = commitment.Id;
        OpenInput(InputMode.Note, commitment.Note ?? string.Empty);
    }

    // ---- Painting ----

    private static readonly Color ClosedTextColor = Color.FromArgb(255, 150, 150, 156);

    /// <summary>Commitment text (plus its estimate, when it has one) and either the three close-out
    /// buttons or the outcome it was closed with. A closed row's text is dimmed, and struck through
    /// when it's actually Done. Alternates ThemedListRow/ThemedListRowDark by index, same as every
    /// other list on this base.</summary>
    protected override void PaintListRow(Graphics g, int index, Rectangle rowRect)
    {
        var commitment = _today[index];
        var open = commitment.Outcome == Outcome.Open;

        using (var rowFill = new SolidBrush(index % 2 == 0 ? ThemedListRow : ThemedListRowDark))
            g.FillRectangle(rowFill, rowRect);

        var text = commitment.EstimateMinutes is { } minutes ? $"{commitment.Text}  {minutes}m" : commitment.Text;
        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(open ? Color.WhiteSmoke : ClosedTextColor))
        using (var textFormat = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(text, commitment.Outcome == Outcome.Done ? _closedRowFont : Font, textBrush, GetRowTextArea(rowRect, index), textFormat);
        }
        g.TextRenderingHint = previousTextHint;

        if (open)
        {
            PaintOutcomeGlyph(g, GetRowButtonRect(rowRect, 2), Outcome.Done, Color.WhiteSmoke);
            PaintOutcomeGlyph(g, GetRowButtonRect(rowRect, 1), Outcome.Partial, Color.WhiteSmoke);
            PaintOutcomeGlyph(g, GetRowButtonRect(rowRect, 0), Outcome.Skipped, Color.WhiteSmoke);
        }
        else
        {
            PaintOutcomeGlyph(g, GetRowButtonRect(rowRect, 0), commitment.Outcome, ClosedTextColor);
        }
    }

    /// <summary>Check (done), half-filled circle (partly done), cross (skipped), dash (no check-in) -
    /// drawn with plain GDI+ rather than Unicode symbols, same reasoning as WarningIcon.</summary>
    private static void PaintOutcomeGlyph(Graphics g, Rectangle rect, Outcome outcome, Color color)
    {
        var previousSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float cx = rect.X + rect.Width / 2f;
        float cy = rect.Y + rect.Height / 2f;
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        switch (outcome)
        {
            case Outcome.Done:
                g.DrawLines(pen, new[] { new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 4) });
                break;
            case Outcome.Partial:
                var circle = new RectangleF(cx - 5, cy - 5, 10, 10);
                using (var fill = new SolidBrush(color))
                    g.FillPie(fill, circle.X, circle.Y, circle.Width, circle.Height, 90, 180);
                g.DrawEllipse(pen, circle);
                break;
            case Outcome.Skipped:
                g.DrawLine(pen, cx - 4, cy - 4, cx + 4, cy + 4);
                g.DrawLine(pen, cx + 4, cy - 4, cx - 4, cy + 4);
                break;
            case Outcome.NoCheckIn:
                g.DrawLine(pen, cx - 4, cy, cx + 4, cy);
                break;
        }

        g.SmoothingMode = previousSmoothing;
    }

    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        PaintChrome(g, contentWidth, contentHeight);

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var statusBrush = new SolidBrush(_inputMode == InputMode.None && _flash is null ? ClosedTextColor : Color.WhiteSmoke))
        using (var statusFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(StatusText(), Font, statusBrush, ToWindow(GetStatusRect(contentWidth, contentHeight)), statusFormat);
        }
        g.TextRenderingHint = previousTextHint;

        _rowTooltip.Paint(g, Font, SettingsMenuTooltipColor, ToWindow(new Rectangle(0, 0, contentWidth, contentHeight)),
            Style.HeaderBorderMode ? ThemedTitle : null);
    }
}
