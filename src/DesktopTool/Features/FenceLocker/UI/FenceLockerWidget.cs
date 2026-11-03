using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json;
using DesktopTool.Features.Fences;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.FenceLocker.UI;

/// <summary>Persisted state for the Fence Locker widget itself (position, size, title, visibility,
/// style). The locks live in FenceLockStore, the loadouts in LockLoadoutLibrary.</summary>
public sealed class FenceLockerModel : WidgetStyleModel
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 340;
    public int? Height { get; set; }
    public string Title { get; set; } = "Fence Locker";
    public bool Visible { get; set; } = true;
}

/// <summary>Same shape as LayoutLauncherStore, for the single FenceLockerModel.</summary>
public sealed class FenceLockerStore
{
    private static readonly string FilePath = Path.Combine(FenceLockerJson.DirectoryPath, "fence-locker.json");
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public FenceLockerModel Load()
    {
        if (!File.Exists(FilePath))
            return new FenceLockerModel();
        try
        {
            return JsonSerializer.Deserialize<FenceLockerModel>(File.ReadAllText(FilePath), SerializerOptions) ?? new FenceLockerModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new FenceLockerModel();
        }
    }

    public void Save(FenceLockerModel model)
    {
        Directory.CreateDirectory(FenceLockerJson.DirectoryPath);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(model, SerializerOptions));
    }
}

/// <summary>
/// "Fence Locker" widget - your saved lock loadouts. Click a loadout to place it: the screen turns
/// into a picker and every fence you click gets that lock (see FencePickerOverlay). Each row's cog
/// edits or deletes the loadout. Along the bottom: Add Lock (a new loadout, starting with no ways
/// in), Manage Locks (the fences that are locked right now - see ManageLocksForm) and Unlock History.
/// Same chrome and row plumbing as JunkDrawerWidget.
/// </summary>
internal sealed class FenceLockerWidget : LayeredWidgetForm
{
    private const int OuterMarginPx = 13;
    private const int HeaderHeight = 28;
    private const int ButtonBandOverhang = 19;
    private const int TopMarginWithButtons = OuterMarginPx + ButtonBandOverhang;

    private const int ListVerticalPadding = 8;
    private const int ListHorizontalPadding = 10;
    private const int ListRowHeightConst = 38;
    private const int DefaultVisibleRows = 4;
    private const int CogSize = 20;

    private const int BottomRowHeight = 26;
    private const int BottomRowGap = 8;
    private const int BottomRowBottomPadding = 12;
    private static readonly int[] BottomRowWidths = { 80, 104, 116 };

    private enum RowTarget { None, Place, Edit }

    private readonly FenceLockerModel _model;
    private readonly FenceLockerStore _store;
    private readonly PaintedTooltip _rowTooltip = new();

    private bool _allowClose;
    private bool _settingsButtonArmed;
    private RowTarget _armedTarget = RowTarget.None;
    private int _armedRowIndex = -1;

    protected override IReadOnlyList<ChromeButton> ExtraButtons { get; }

    public event EventHandler? VisibilityChanged;

    protected override int OuterMargin => OuterMarginPx;
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMarginWithButtons;
    protected override int BottomBand => ButtonRowAtBottom ? TopMarginWithButtons : OuterMargin;
    protected override int MaxTopBand => TopMarginWithButtons;
    protected override IWidgetStyle Style => _model;
    protected override string HelpEntry => "Fence Locker";

    private static IReadOnlyList<LockLoadout> Loadouts => FenceLocks.Loadouts.Loadouts;

    private static int DefaultBodyHeight => HeaderHeight + ListVerticalPadding * 2 + DefaultVisibleRows * ListRowHeightConst
        + BottomRowGap + BottomRowHeight + BottomRowBottomPadding;

    private const int MinWidthForButtons = 340;

    public FenceLockerWidget(FenceManager fenceManager, FenceLockerModel model, FenceLockerStore store)
        : base(model.Opacity / 100f, fenceManager)
    {
        _model = model;
        _store = store;

        // A size saved before the three bottom buttons existed would stack them - widen to fit.
        if (_model.Width < MinWidthForButtons)
        {
            _model.Width = MinWidthForButtons;
            Persist();
        }

        FenceLocks.Loadouts.Changed += OnLoadoutsChanged;

        ExtraButtons = new List<ChromeButton>
        {
            new("×", 22, HideAndPersist, "Hide Fence Locker"),
        };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = AppTheme.Font;

        RenderAndPresent();
    }

    private void OnLoadoutsChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed)
            RenderAndPresent();
    }

    public void RefreshRowStates() => RenderAndPresent();

    /// <summary>"Locked", "Locked · opens today at 18:00", "Open (time window)", "Unlocked this
    /// session" or "No lock" - shared with ManageLocksForm.</summary>
    internal static string StatusText(Guid fenceId)
    {
        var service = FenceLocks.Service;
        switch (service.GetStatus(fenceId))
        {
            case FenceLockStatus.None:
                return "No lock";
            case FenceLockStatus.OpenByTime:
                return "Open (time window)";
            case FenceLockStatus.UnlockedThisSession:
                return "Unlocked this session";
            default:
                var now = service.Now;
                var next = service.GetLock(fenceId)?.Criteria
                    .OfType<Criteria.TimeWindowCriterion>()
                    .Select(c => c.NextOpening(now))
                    .Where(d => d is not null)
                    .Min();
                return next is { } opening ? $"Locked · opens {CriterionUi.FormatOpening(opening, now)}" : "Locked";
        }
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

    protected override void DisposeOwnedResources() => FenceLocks.Loadouts.Changed -= OnLoadoutsChanged;

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

        var (target, index) = GetRowTargetAt(contentPoint);
        _armedTarget = target;
        _armedRowIndex = index;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateListScrollDrag(ToContent(e.Location));
        UpdateRowTooltip(e.Location);
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

        if (_armedTarget == RowTarget.None)
            return;
        var armedTarget = _armedTarget;
        var armedIndex = _armedRowIndex;
        _armedTarget = RowTarget.None;
        _armedRowIndex = -1;

        var (target, index) = GetRowTargetAt(contentPoint);
        if (target != armedTarget || index != armedIndex || index >= Loadouts.Count)
            return;

        var loadout = Loadouts[index];
        _rowTooltip.Hide();
        RenderAndPresent();
        // After this mouse-up finishes, so the editor or picker doesn't open mid-click.
        if (target == RowTarget.Edit)
            BeginInvoke(() => LockEditorForm.EditLoadout(loadout, this));
        else
            BeginInvoke(() => FencePickerOverlay.Place(loadout));
    }

    // ---- Rows ----

    protected override IReadOnlyList<ContentButton> GetContentButtons(int contentWidth, int contentHeight)
    {
        var top = contentHeight - BottomRowBottomPadding - RowHeight(contentWidth, BottomRowHeight, BottomRowGap, BottomRowWidths);
        var rects = LayoutRow(contentWidth, top, BottomRowHeight, BottomRowGap, BottomRowWidths);
        return new[]
        {
            new ContentButton("Add Lock", rects[0], () => LockEditorForm.EditLoadout(null, this)),
            new ContentButton("Manage Locks", rects[1], () => ManageLocksForm.Open(this)),
            new ContentButton("Unlock History...", rects[2], () => UnlockHistoryForm.Open(this)),
        };
    }

    protected override Rectangle GetListArea(int contentWidth, int contentHeight)
    {
        var top = (_model.HideHeader ? 0 : HeaderHeight) + ListVerticalPadding;
        var buttonRow = BottomRowGap + BottomRowBottomPadding + RowHeight(contentWidth, BottomRowHeight, BottomRowGap, BottomRowWidths);
        var height = Math.Max(ListRowHeight, contentHeight - top - ListVerticalPadding - buttonRow);
        return new Rectangle(ListHorizontalPadding, top, contentWidth - ListHorizontalPadding * 2, height);
    }

    protected override int ListRowCount => Loadouts.Count;
    protected override int ListRowHeight => ListRowHeightConst;

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

    private static Rectangle GetCogRect(Rectangle rowRect) =>
        new(rowRect.Right - 6 - CogSize, rowRect.Y + (rowRect.Height - CogSize) / 2, CogSize, CogSize);

    private (RowTarget Target, int Index) GetRowTargetAt(Point contentPoint)
    {
        if (!TryGetRowAt(contentPoint, out var index, out var rowRect))
            return (RowTarget.None, -1);
        return GetCogRect(rowRect).Contains(contentPoint) ? (RowTarget.Edit, index) : (RowTarget.Place, index);
    }

    private void UpdateRowTooltip(Point windowLocation)
    {
        var contentPoint = ToContent(windowLocation);
        bool changed;
        if (TryGetRowAt(contentPoint, out var index, out var rowRect) && index < Loadouts.Count)
        {
            var name = Loadouts[index].Name;
            var cog = GetCogRect(rowRect);
            changed = cog.Contains(contentPoint)
                ? _rowTooltip.Show($"Edit \"{name}\"", ToWindow(cog))
                : _rowTooltip.Show($"Click fences to lock them with \"{name}\"", ToWindow(rowRect));
        }
        else
        {
            changed = _rowTooltip.Hide();
        }
        if (changed)
            RenderAndPresent();
    }

    /// <summary>Padlock, the loadout's name, its ways in underneath, and an edit cog on the right.</summary>
    protected override void PaintListRow(Graphics g, int index, Rectangle rowRect)
    {
        if (index >= Loadouts.Count)
            return;
        var loadout = Loadouts[index];

        var rowBackground = index % 2 == 0 ? ThemedListRow : ThemedListRowDark;
        using (var rowFill = new SolidBrush(rowBackground))
            g.FillRectangle(rowFill, rowRect);

        const int iconSize = 13;
        var iconRect = new Rectangle(rowRect.X + 9, rowRect.Y + (rowRect.Height - iconSize) / 2, iconSize, iconSize);
        PadlockIcon.Paint(g, iconRect, Color.WhiteSmoke, open: false);

        var cog = GetCogRect(rowRect);
        var textLeft = iconRect.Right + 10;
        var textWidth = Math.Max(0, cog.X - 6 - textLeft);
        var nameRect = new Rectangle(textLeft, rowRect.Y + 3, textWidth, rowRect.Height / 2);
        var detailRect = new Rectangle(textLeft, rowRect.Y + rowRect.Height / 2 - 1, textWidth, rowRect.Height / 2 - 2);

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var nameBrush = new SolidBrush(Color.WhiteSmoke))
        using (var detailBrush = new SolidBrush(AppTheme.DisabledText))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var detailFont = new Font(Font.FontFamily, Font.Size - 1f))
        {
            g.DrawString(loadout.Name, Font, nameBrush, nameRect, format);
            g.DrawString(loadout.Describe(), detailFont, detailBrush, detailRect, format);
        }
        g.TextRenderingHint = previousTextHint;

        PaintCogGlyph(g, cog);
    }

    /// <summary>The same simplified gear Widget Manager's rows use for "edit".</summary>
    private static void PaintCogGlyph(Graphics g, Rectangle rect)
    {
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        var outerRadius = Math.Min(rect.Width, rect.Height) * 0.2f;
        var innerRadius = outerRadius * 0.45f;
        var toothLength = outerRadius * 0.5f;
        var toothWidth = outerRadius * 0.55f;
        const int toothCount = 8;

        var previousSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.WhiteSmoke, 1.3f))
        {
            g.DrawEllipse(pen, cx - outerRadius, cy - outerRadius, outerRadius * 2, outerRadius * 2);
            g.DrawEllipse(pen, cx - innerRadius, cy - innerRadius, innerRadius * 2, innerRadius * 2);
        }
        using var toothBrush = new SolidBrush(Color.WhiteSmoke);
        for (var i = 0; i < toothCount; i++)
        {
            var angle = i * (Math.PI * 2 / toothCount);
            var midRadius = outerRadius + toothLength / 2f;
            var state = g.Save();
            g.TranslateTransform(cx + (float)(Math.Cos(angle) * midRadius), cy + (float)(Math.Sin(angle) * midRadius));
            g.RotateTransform((float)(angle * 180 / Math.PI));
            g.FillRectangle(toothBrush, -toothLength / 2f, -toothWidth / 2f, toothLength, toothWidth);
            g.Restore(state);
        }
        g.SmoothingMode = previousSmoothing;
    }

    // ---- Title/header plumbing (same as JunkDrawerWidget) ----

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

    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        PaintChrome(g, contentWidth, contentHeight);
        if (Loadouts.Count == 0)
        {
            using var brush = new SolidBrush(AppTheme.DisabledText);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("No locks yet - use Add Lock to make one,\nthen click it to place it on fences.", Font, brush,
                ToWindow(GetListArea(contentWidth, contentHeight)), format);
        }
        _rowTooltip.Paint(g, Font, SettingsMenuTooltipColor, ToWindow(new Rectangle(0, 0, contentWidth, contentHeight)),
            Style.HeaderBorderMode ? ThemedTitle : null);
    }
}
