using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Wallpapers.Native;
using DesktopTool.Native;
using DesktopTool.UI;
using Microsoft.Win32;

namespace DesktopTool.Features.Wallpapers.UI;

/// <summary>
/// "Wallpapers" widget: a mini map of the monitors, drawn in their real arrangement, each tile
/// showing that monitor's current background. Clicking a tile opens a menu to give that monitor its
/// own single image, solid color, or slideshow (see WallpaperService for how each is applied).
/// The one Windows-wide setting, how images are fitted, lives in the Settings menu. Same chrome,
/// move/resize/snap and hide-instead-of-close plumbing as LayoutLauncherWidget.
/// </summary>
internal sealed class WallpaperWidget : LayeredWidgetForm
{
    private const int OuterMarginPx = 13;
    private const int HeaderHeight = 28;
    private const int ButtonBandOverhang = 19;
    private const int TopMarginWithButtons = OuterMarginPx + ButtonBandOverhang;
    private const int DefaultBodyHeight = 150;

    private const int MapPadding = 10;
    private const int TileGap = 6;
    private const int ThumbnailWidth = 320;

    // Tile menu command ids (separate menu from Settings, so these only need to be unique here).
    private const int CmdImage = 1;
    private const int CmdColorCustom = 2;
    private const int CmdColorEyedrop = 3;
    private const int CmdSlideshowFolder = 4;
    private const int CmdSlideshowNext = 5;
    private const int CmdSlideshowShuffle = 6;
    private const int CmdColorBase = 100;
    private const int CmdIntervalBase = 200;

    // Settings-menu command ids for the fit rows - positive, so they never collide with
    // LayeredWidgetForm's own (all negative) ids.
    private const int CmdFitBase = 1;

    private static readonly (WallpaperFit Fit, string Label)[] FitOptions =
    {
        (WallpaperFit.Fill, "Fill"),
        (WallpaperFit.Fit, "Fit"),
        (WallpaperFit.Stretch, "Stretch"),
        (WallpaperFit.Center, "Center"),
        (WallpaperFit.Tile, "Tile"),
    };

    private static readonly (int Minutes, string Label)[] Intervals =
    {
        (1, "1 minute"), (5, "5 minutes"), (15, "15 minutes"), (30, "30 minutes"),
        (60, "1 hour"), (360, "6 hours"), (1440, "1 day"),
    };

    // Neutrals first (the usual picks for a plain desktop), then the app's shared tint presets.
    private static readonly (Color Color, string Name)[] Palette =
        new[] { (Color.Black, "Black"), (Color.FromArgb(32, 32, 36), "Charcoal"), (Color.FromArgb(128, 128, 128), "Gray"), (Color.White, "White") }
            .Concat(StyleTint.Presets.Select((c, i) => (c, StyleTint.PresetNames[i])))
            .ToArray();

    private readonly WallpaperService _service;
    private readonly WallpaperWidgetModel _model;
    private readonly WallpaperWidgetStore _store;

    private readonly Dictionary<string, Bitmap?> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly PaintedTooltip _tooltip = new();

    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private string? _loadError;
    private WallpaperFit _currentFit = WallpaperFit.Fill;

    private bool _allowClose;
    private bool _settingsButtonArmed;
    private int _armedTile = -1;
    private int _hoverTile = -1;
    private DropdownMenu? _tileMenu;

    protected override IReadOnlyList<ChromeButton> ExtraButtons { get; }

    /// <summary>Fired whenever Visible actually changes, so Widget Manager's row switch stays current
    /// (see LayoutLauncherWidget.VisibilityChanged).</summary>
    public event EventHandler? VisibilityChanged;

    protected override int OuterMargin => OuterMarginPx;
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMarginWithButtons;
    protected override int BottomBand => ButtonRowAtBottom ? TopMarginWithButtons : OuterMargin;
    protected override int MaxTopBand => TopMarginWithButtons;
    protected override IWidgetStyle Style => _model;

    public WallpaperWidget(WallpaperService service, FenceManager fenceManager, WallpaperWidgetModel model, WallpaperWidgetStore store)
        : base(model.Opacity / 100f, fenceManager)
    {
        _service = service;
        _model = model;
        _store = store;

        _service.Changed += OnServiceChanged;
        _service.SlideshowProblemsChanged += OnServiceChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        ExtraButtons = new List<ChromeButton>
        {
            new("×", 22, HideAndPersist, "Hide Wallpapers"),
        };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = AppTheme.Font;

        RefreshMonitors();
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
        RefreshMonitors();
        Show();
        Activate();
        _model.Visible = true;
        Persist();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HideAndPersist()
    {
        _tileMenu?.Close();
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

    protected override void DisposeOwnedResources()
    {
        _service.Changed -= OnServiceChanged;
        _service.SlideshowProblemsChanged -= OnServiceChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        foreach (var thumbnail in _thumbnails.Values)
            thumbnail?.Dispose();
        _thumbnails.Clear();
    }

    // ---- Monitor state ----

    /// <summary>Re-reads every monitor, its current image and the fit mode from Windows, drops
    /// thumbnails no monitor shows any more, and repaints.</summary>
    private void RefreshMonitors()
    {
        try
        {
            _monitors = DesktopWallpaper.GetMonitors();
            _currentFit = DesktopWallpaper.GetFit();
            _loadError = null;
        }
        catch (WallpaperException ex)
        {
            _monitors = Array.Empty<MonitorInfo>();
            _loadError = ex.Message;
        }

        var inUse = new HashSet<string>(_monitors.Select(m => m.CurrentImage).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in _thumbnails.Keys.Where(p => !inUse.Contains(p)).ToList())
        {
            _thumbnails[path]?.Dispose();
            _thumbnails.Remove(path);
        }

        _hoverTile = Math.Min(_hoverTile, _monitors.Count - 1);
        if (IsHandleCreated)
            RenderAndPresent();
    }

    private void OnServiceChanged(object? sender, EventArgs e) => RefreshMonitors();

    // SystemEvents fire on their own thread - hop back onto the UI thread first.
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Desktop)
            PostRefresh();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => PostRefresh();

    private void PostRefresh()
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(RefreshMonitors);
    }

    /// <summary>"Display 2" from Screen's \\.\DISPLAY2 device name, matched to this monitor by bounds;
    /// falls back to list order if no Screen matches (shouldn't happen on a PerMonitorV2 app).</summary>
    private (string Name, bool Primary) DescribeMonitor(int index)
    {
        var bounds = _monitors[index].Bounds;
        var screen = Screen.AllScreens.FirstOrDefault(s => s.Bounds == bounds);
        var number = screen?.DeviceName is { } device && device.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase)
            ? device[@"\\.\DISPLAY".Length..]
            : (index + 1).ToString();
        return ($"Display {number}", screen?.Primary ?? false);
    }

    private BackgroundKind KindOf(MonitorInfo monitor) =>
        _service.GetBackground(monitor.DeviceId)?.Kind ?? BackgroundKind.Image;

    // ---- Geometry / hit-testing ----

    private Rectangle GetMapArea(int contentWidth, int contentHeight)
    {
        var top = (_model.HideHeader ? 0 : HeaderHeight) + MapPadding;
        return Rectangle.FromLTRB(MapPadding, top, contentWidth - MapPadding, contentHeight - MapPadding);
    }

    private IReadOnlyList<Rectangle> GetTileRects()
    {
        var size = GetContentSize();
        return MonitorMapLayout.Fit(_monitors.Select(m => m.Bounds).ToList(), GetMapArea(size.Width, size.Height), TileGap);
    }

    private int TileAt(Point contentPoint)
    {
        var tiles = GetTileRects();
        for (var i = 0; i < tiles.Count; i++)
            if (tiles[i].Contains(contentPoint))
                return i;
        return -1;
    }

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

        _armedTile = TileAt(contentPoint);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var contentPoint = ToContent(e.Location);
        var tile = TileAt(contentPoint);

        var changed = tile != _hoverTile;
        _hoverTile = tile;
        changed |= tile >= 0
            ? _tooltip.Show(TooltipText(tile), ToWindow(GetTileRects()[tile]))
            : _tooltip.Hide();

        if (changed)
            RenderAndPresent();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        var changed = _tooltip.Hide() | _hoverTile >= 0;
        _hoverTile = -1;
        if (changed)
            RenderAndPresent();
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

        if (_armedTile >= 0)
        {
            var armed = _armedTile;
            _armedTile = -1;
            if (TileAt(contentPoint) == armed)
                OpenTileMenu(armed);
        }
    }

    private string TooltipText(int index)
    {
        var monitor = _monitors[index];
        var (name, primary) = DescribeMonitor(index);
        var header = $"{name}{(primary ? " (main)" : "")} · {monitor.Bounds.Width}×{monitor.Bounds.Height}";

        var background = _service.GetBackground(monitor.DeviceId);
        var detail = KindOf(monitor) switch
        {
            BackgroundKind.Color => "Solid color",
            BackgroundKind.Slideshow when _service.GetSlideshowProblem(monitor.DeviceId) is { } problem => $"Slideshow problem: {problem}",
            BackgroundKind.Slideshow => $"Slideshow: {Path.GetFileName(background?.SlideshowFolder?.TrimEnd('\\'))} (every {IntervalLabel(background?.IntervalMinutes ?? 30)})",
            _ => monitor.CurrentImage is { } image ? Path.GetFileName(image) : "No image",
        };
        return $"{header} · {detail}";
    }

    private static string IntervalLabel(int minutes) =>
        Intervals.FirstOrDefault(i => i.Minutes == minutes).Label ?? $"{minutes} minutes";

    // ---- Tile menu ----

    private void OpenTileMenu(int index)
    {
        if (index < 0 || index >= _monitors.Count)
            return;

        _tileMenu?.Close();
        var monitor = _monitors[index];
        var deviceId = monitor.DeviceId;
        var (name, _) = DescribeMonitor(index);
        var kind = KindOf(monitor);

        var colorRows = new List<DropdownMenu.Row> { new(0, "Solid Color", IsHeader: true) };
        for (var i = 0; i < Palette.Length; i++)
        {
            var argb = Palette[i].Color.ToArgb();
            colorRows.Add(new DropdownMenu.Row(CmdColorBase + i, string.Empty, IsGridItem: true, Swatch: Palette[i].Color,
                IsChecked: () => KindOf(monitor) == BackgroundKind.Color && _service.GetBackground(deviceId)?.ColorArgb == argb,
                Tooltip: Palette[i].Name));
        }
        colorRows.Add(new DropdownMenu.Row(CmdColorCustom, string.Empty, IsGridItem: true, Glyph: DropdownMenu.GridGlyph.Plus, Tooltip: "Custom..."));
        colorRows.Add(new DropdownMenu.Row(CmdColorEyedrop, string.Empty, IsGridItem: true, Glyph: DropdownMenu.GridGlyph.Eyedropper, Tooltip: "Eyedropper"));

        var slideshowRows = new List<DropdownMenu.Row>
        {
            new(CmdSlideshowFolder, "Choose Folder..."),
        };
        if (kind == BackgroundKind.Slideshow)
            slideshowRows.Add(new DropdownMenu.Row(CmdSlideshowNext, "Next Image"));
        slideshowRows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        slideshowRows.Add(new DropdownMenu.Row(0, "Change Every", IsHeader: true));
        for (var i = 0; i < Intervals.Length; i++)
        {
            var minutes = Intervals[i].Minutes;
            slideshowRows.Add(new DropdownMenu.Row(CmdIntervalBase + i, Intervals[i].Label, HasCheckbox: true,
                IsChecked: () => (_service.GetBackground(deviceId)?.IntervalMinutes ?? 30) == minutes));
        }
        slideshowRows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        slideshowRows.Add(new DropdownMenu.Row(CmdSlideshowShuffle, "Shuffle", HasCheckbox: true,
            IsChecked: () => _service.GetBackground(deviceId)?.Shuffle ?? false));

        var rows = new List<DropdownMenu.Row>
        {
            new(0, $"{name} · {monitor.Bounds.Width}×{monitor.Bounds.Height}", IsHeader: true),
            new(CmdImage, "Image...", HasCheckbox: true, IsChecked: () => KindOf(monitor) == BackgroundKind.Image),
            new(0, "Solid Color", HasCheckbox: true, IsChecked: () => KindOf(monitor) == BackgroundKind.Color, Submenu: colorRows),
            new(0, "Slideshow", HasCheckbox: true, IsChecked: () => KindOf(monitor) == BackgroundKind.Slideshow, Submenu: slideshowRows),
        };

        var tileRect = GetTileRects()[index];
        var anchor = RectangleToScreen(ToWindow(tileRect));
        var menu = new DropdownMenu(rows, anchor, preferLeft: false, Font,
            () => SettingsMenuFieldColor, () => SettingsMenuHoverColor, () => SettingsMenuAccentColor,
            () => SettingsMenuBorderColor, () => SettingsMenuTooltipColor);
        _tileMenu = menu;
        menu.ItemClicked += id => HandleTileCommand(menu, monitor, id);
        Activation.MenuOpen = true;
        menu.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_tileMenu, menu))
                _tileMenu = null;
            Activation.MenuOpen = false;
            RenderOpacity.BeginIfNeeded();
        };
        menu.Show(this);
        RenderOpacity.BeginIfNeeded();
    }

    /// <summary>Settings that just toggle (interval, shuffle) keep the menu open so several can be
    /// changed in a row; anything that opens a dialog or changes the picture closes it first, then
    /// runs once the menu is gone so the dialog isn't stuck behind it.</summary>
    private void HandleTileCommand(DropdownMenu menu, MonitorInfo monitor, int id)
    {
        if (id >= CmdIntervalBase && id < CmdIntervalBase + Intervals.Length)
        {
            _service.SetInterval(monitor.DeviceId, Intervals[id - CmdIntervalBase].Minutes);
            menu.RefreshChecks();
            return;
        }
        if (id == CmdSlideshowShuffle)
        {
            _service.SetShuffle(monitor.DeviceId, !(_service.GetBackground(monitor.DeviceId)?.Shuffle ?? false));
            menu.RefreshChecks();
            return;
        }

        menu.Close();
        BeginInvoke(() => RunTileAction(monitor, id));
    }

    private void RunTileAction(MonitorInfo monitor, int id)
    {
        try
        {
            switch (id)
            {
                case CmdImage:
                    if (PickImage(monitor) is { } image)
                        _service.ApplyImage(monitor, image);
                    break;
                case CmdColorCustom:
                    using (var dialog = new ColorDialog { FullOpen = true, Color = CurrentColor(monitor) })
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                            _service.ApplyColor(monitor, dialog.Color);
                    }
                    break;
                case CmdColorEyedrop:
                    EyedropperOverlay.Pick(color => ApplySafely(() => _service.ApplyColor(monitor, color)));
                    break;
                case CmdSlideshowFolder:
                    using (var dialog = new FolderBrowserDialog
                    {
                        Description = "Choose a folder of images for the slideshow",
                        UseDescriptionForTitle = true,
                        SelectedPath = _service.GetBackground(monitor.DeviceId)?.SlideshowFolder ?? string.Empty,
                    })
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                            _service.StartSlideshow(monitor, dialog.SelectedPath);
                    }
                    break;
                case CmdSlideshowNext:
                    _service.NextSlide(monitor.DeviceId);
                    break;
                default:
                    if (id >= CmdColorBase && id < CmdColorBase + Palette.Length)
                        _service.ApplyColor(monitor, Palette[id - CmdColorBase].Color);
                    break;
            }
        }
        catch (Exception ex) when (ex is WallpaperException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }

    private void ApplySafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is WallpaperException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Wallpapers", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private Color CurrentColor(MonitorInfo monitor) =>
        _service.GetBackground(monitor.DeviceId) is { Kind: BackgroundKind.Color } background
            ? Color.FromArgb(background.ColorArgb)
            : Color.Black;

    private string? PickImage(MonitorInfo monitor)
    {
        var current = monitor.CurrentImage;
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a wallpaper image",
            Filter = "Images|" + string.Join(";", SlideshowPicker.ImageExtensions.Select(e => "*" + e)) + "|All files|*.*",
            InitialDirectory = current is not null && File.Exists(current) ? Path.GetDirectoryName(current) : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    // ---- Settings menu: fit mode ----

    protected override IReadOnlyList<DropdownMenu.Row>? BuildAdditionalSettingsRows()
    {
        var rows = new List<DropdownMenu.Row> { new(0, "Fit (all monitors)", IsHeader: true) };
        foreach (var (fit, label) in FitOptions)
            rows.Add(new DropdownMenu.Row(CmdFitBase + (int)fit, label, HasCheckbox: true, IsChecked: () => _currentFit == fit));
        return rows;
    }

    protected override void HandleSettingsCommand(int id)
    {
        var fit = (WallpaperFit)(id - CmdFitBase);
        if (id >= CmdFitBase && Enum.IsDefined(fit))
        {
            try
            {
                DesktopWallpaper.SetFit(fit);
                _currentFit = fit;
            }
            catch (WallpaperException ex)
            {
                ShowError(ex.Message);
            }
            return;
        }

        base.HandleSettingsCommand(id);
    }

    // ---- Title/header plumbing (same as LayoutLauncherWidget) ----

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

    // ---- Painting ----

    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        PaintChrome(g, contentWidth, contentHeight);

        // g is in window coordinates; the map's tile rects (shared with hit-testing) are content-relative.
        var state = g.Save();
        var origin = ToWindow(Point.Empty);
        g.TranslateTransform(origin.X, origin.Y);
        PaintMap(g, contentWidth, contentHeight);
        g.Restore(state);

        _tooltip.Paint(g, Font, SettingsMenuTooltipColor, ToWindow(new Rectangle(0, 0, contentWidth, contentHeight)),
            Style.HeaderBorderMode ? ThemedTitle : null);
    }

    private void PaintMap(Graphics g, int contentWidth, int contentHeight)
    {
        var previousHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        try
        {
            if (_monitors.Count == 0)
            {
                using var brush = new SolidBrush(AppTheme.DisabledText);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(_loadError ?? "No monitors found", Font, brush, GetMapArea(contentWidth, contentHeight), format);
                return;
            }

            var tiles = GetTileRects();
            for (var i = 0; i < tiles.Count; i++)
                PaintTile(g, i, tiles[i]);
        }
        finally
        {
            g.TextRenderingHint = previousHint;
        }
    }

    private void PaintTile(Graphics g, int index, Rectangle tile)
    {
        var monitor = _monitors[index];
        var kind = KindOf(monitor);

        if (kind == BackgroundKind.Color && _service.GetBackground(monitor.DeviceId) is { } colored)
        {
            using var fill = new SolidBrush(Color.FromArgb(colored.ColorArgb));
            g.FillRectangle(fill, tile);
        }
        else if (monitor.CurrentImage is { } path && GetThumbnail(path) is { } thumbnail)
        {
            DrawCropped(g, thumbnail, tile);
        }
        else
        {
            using var fill = new SolidBrush(Color.FromArgb(24, 24, 28));
            g.FillRectangle(fill, tile);
            using var brush = new SolidBrush(AppTheme.DisabledText);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("No image", Font, brush, tile, format);
        }

        var hovered = index == _hoverTile;
        using (var border = new Pen(hovered ? Accent : Color.FromArgb(160, 0, 0, 0), hovered ? 2f : 1f))
            g.DrawRectangle(border, tile.X, tile.Y, tile.Width - 1, tile.Height - 1);

        var (name, primary) = DescribeMonitor(index);
        var badgeText = name["Display ".Length..] + (primary ? " ★" : "");
        PaintBadge(g, badgeText, new Point(tile.X + 4, tile.Y + 4), alignRight: false);

        var kindText = kind switch
        {
            BackgroundKind.Slideshow => "▶",
            BackgroundKind.Color => "●",
            _ => null,
        };
        if (kindText is not null)
            PaintBadge(g, kindText, new Point(tile.Right - 4, tile.Y + 4), alignRight: true);

        if (kind == BackgroundKind.Slideshow && _service.GetSlideshowProblem(monitor.DeviceId) is not null)
            WarningIcon.Paint(g, new Rectangle(tile.Right - 20, tile.Bottom - 20, 16, 16), AppTheme.Warning);
    }

    private void PaintBadge(Graphics g, string text, Point anchor, bool alignRight)
    {
        var size = g.MeasureString(text, Font);
        var width = (int)Math.Ceiling(size.Width) + 6;
        var height = (int)Math.Ceiling(size.Height) + 2;
        var rect = new Rectangle(alignRight ? anchor.X - width : anchor.X, anchor.Y, width, height);

        using (var path = RoundedRect(rect, 4))
        using (var fill = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
        {
            var previousSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(fill, path);
            g.SmoothingMode = previousSmoothing;
        }

        using var brush = new SolidBrush(Color.WhiteSmoke);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, Font, brush, rect, format);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Draws image scaled to cover tile entirely, cropping whichever dimension overflows -
    /// the same look as Windows' own Fill mode, which reads best at thumbnail size.</summary>
    private static void DrawCropped(Graphics g, Image image, Rectangle tile)
    {
        var scale = Math.Max((double)tile.Width / image.Width, (double)tile.Height / image.Height);
        var srcWidth = tile.Width / scale;
        var srcHeight = tile.Height / scale;
        var src = new RectangleF((float)((image.Width - srcWidth) / 2), (float)((image.Height - srcHeight) / 2), (float)srcWidth, (float)srcHeight);

        var previousMode = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(image, tile, src, GraphicsUnit.Pixel);
        g.InterpolationMode = previousMode;
    }

    /// <summary>A small cached copy of path, loaded once through a stream (so the file is never left
    /// locked) and shrunk to ThumbnailWidth. Null - also cached, so it isn't retried every paint -
    /// when the file is missing or isn't an image GDI+ can read.</summary>
    private Bitmap? GetThumbnail(string path)
    {
        if (_thumbnails.TryGetValue(path, out var cached))
            return cached;

        Bitmap? thumbnail = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var original = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            var width = Math.Min(ThumbnailWidth, original.Width);
            var height = Math.Max(1, (int)Math.Round(original.Height * (double)width / original.Width));
            thumbnail = new Bitmap(width, height);
            using var g = Graphics.FromImage(thumbnail);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(original, 0, 0, width, height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            thumbnail?.Dispose();
            thumbnail = null;
        }

        _thumbnails[path] = thumbnail;
        return thumbnail;
    }
}
