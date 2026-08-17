using DesktopTool.Features.Layouts.Native;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.Layouts.UI;

/// <summary>
/// "Manage Layouts..." window - create/rename/delete LayoutProfiles, add/remove/edit each one's
/// LayoutEntry rows (program, target monitor, placement), and run a profile immediately to test it.
/// Dark-themed the same way SnapLinePanel is: AppTheme colors, the shared ComboButton for pickers,
/// FlatStyle.Flat buttons - see SnapLinePanel's own class comment for why plain ComboBox/NumericUpDown
/// aren't used here either.
/// </summary>
internal sealed class LayoutEditorForm : Form
{
    private readonly LayoutManager _manager;

    private readonly ListBox _profileList;
    private readonly TextBox _profileNameBox;
    private readonly Button _deleteProfileButton;

    private readonly ListBox _entryList;
    private readonly WarningBanner _warningBanner;
    private readonly Button _removeEntryButton;
    private readonly TextBox _programPathBox;
    private readonly ComboButton _monitorCombo;
    private readonly ComboButton _placementCombo;
    private readonly Label _urlLabel;
    private readonly ListBox _urlList;
    private readonly TextBox _urlInputBox;
    private readonly Button _addUrlButton;
    private readonly Label _commandLabel;
    private readonly ListBox _commandList;
    private readonly TextBox _commandInputBox;
    private readonly Button _addCommandButton;
    private readonly Button _addTabButton;
    private readonly Label _terminalShellLabel;
    private readonly ComboButton _terminalShellCombo;
    private readonly CheckBox _minimizedCheck;
    private readonly Button _runButton;

    private readonly List<Screen> _screens = Screen.AllScreens.ToList();

    // Shown in place of a real screen name, both in the Monitor combo (SelectEntry) and the
    // Programs list row (DescribeEntry), for an entry whose saved TargetMonitor doesn't match any
    // currently connected display - see SelectEntry's own comment for why this needs to be visibly
    // different from "Screen 1" rather than just defaulting to it.
    private const string MonitorNotConnectedLabel = "(monitor not connected)";

    // Shown only for an entry captured as WindowsTerminal.exe (see WindowPlacer.IsWindowsTerminalProgram)
    // - WindowsTerminal.exe isn't itself a shell, so BuildTerminalCommandArgs needs to be told which
    // one to actually run Command's lines in (see LayoutEntry.TerminalShellExe).
    private static readonly (string ExeName, string Display)[] TerminalShellOptions =
    {
        ("powershell.exe", "PowerShell"),
        ("pwsh.exe", "PowerShell 7"),
        ("cmd.exe", "Command Prompt"),
    };

    // Set while this form is itself rewriting a text/combo field from the currently-selected
    // profile/entry - without this, populating _profileNameBox or the entry combos would re-fire
    // their own change handlers and write that same value straight back as if the user had just
    // edited it (harmless here, but SetSelectedIndex firing mid-repopulate could still clobber
    // _selectedEntry's real value with a stale one read before the new entry's fields settle).
    private bool _isPopulating;

    private LayoutProfile? _selectedProfile;
    private LayoutEntry? _selectedEntry;

    /// <summary>initialProfileId selects that profile up front instead of whichever one
    /// RefreshProfileList would otherwise default to (the first) - used to land straight on a
    /// just-captured "Save Current Layout" profile instead of making the user hunt for it in the
    /// list.</summary>
    public LayoutEditorForm(LayoutManager manager, Guid? initialProfileId = null)
    {
        _manager = manager;
        // Layout Launcher's own row can trigger a run (and so a launch error) while this window is
        // already open - without this, DrawProfileListItem's caution glyph would only catch up the
        // next time something else happened to repaint the list (reselecting a profile, say).
        _manager.LaunchFailed += OnLaunchFailed;

        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Manage Layouts";
        ClientSize = new Size(596, 524);
        MaximizeBox = false;
        MinimizeBox = false;
        Font = AppTheme.Font;
        BackColor = AppTheme.Body;
        ForeColor = AppTheme.Text;

        var profilesLabel = new Label { Text = "Layouts", Location = new Point(12, 12), AutoSize = true };
        _profileList = CreateList(new Rectangle(12, 32, 180, 200), drawItem: DrawProfileListItem);
        _profileList.SelectedIndexChanged += (_, _) => SelectProfile(_profileList.SelectedIndex);

        var newProfileButton = new DarkButton { Text = "New", Location = new Point(12, 240), Width = 86 };
        newProfileButton.Click += (_, _) => AddProfile();
        _deleteProfileButton = new DarkButton { Text = "Delete", Location = new Point(106, 240), Width = 86, Enabled = false };
        _deleteProfileButton.Click += (_, _) => DeleteSelectedProfile();

        var nameLabel = new Label { Text = "Name", Location = new Point(12, 276), AutoSize = true };
        _profileNameBox = CreateTextBox(new Rectangle(12, 296, 180, 24));
        _profileNameBox.Leave += (_, _) => CommitProfileName();
        _profileNameBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            CommitProfileName();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };

        var separator = new Panel { Location = new Point(206, 12), Width = 1, Height = 474, BackColor = AppTheme.Border };

        var entriesLabel = new Label { Text = "Programs", Location = new Point(222, 12), AutoSize = true };
        // Shortened from the full 130px it used to occupy - see _warningBanner, which sits
        // in the reclaimed space directly below and only becomes visible when the selected profile
        // actually has something to warn about. A bit shorter still than that banner's own first cut
        // (90 vs 104) to leave room for the banner to wrap onto a second line without colliding
        // with the Select Window/Remove button row underneath.
        _entryList = CreateList(new Rectangle(222, 32, 362, 90), removable: true);
        _entryList.SelectedIndexChanged += (_, _) => SelectEntry(_entryList.SelectedIndex);
        _entryList.MouseDown += (_, e) =>
        {
            if (TryGetRemoveClickIndex(_entryList, e.Location, out var index))
                RemoveEntryAt(index);
        };

        // Yellow banner shown under the Programs list, when the selected profile has at least one
        // entry whose saved TargetMonitor doesn't match any currently connected display (see
        // IsMonitorMissing) and/or its last run left at least one program un-launched (see
        // LayoutManager.GetLaunchError) - text/visibility kept in sync by RefreshWarningBanner,
        // called whenever the selected profile, an entry's monitor assignment, or a run's outcome
        // could have changed. Both problems show at once (one line each) rather than one hiding the
        // other. BackColor's alpha channel actually renders (Label is one of the few WinForms
        // controls that supports a translucent BackColor out of the box), giving the tinted-not-solid
        // look against AppTheme.Body behind it - WarningBanner's own OnPaint relies on that same
        // default OnPaintBackground blend, drawing only the icon and text itself. AutoEllipsis left
        // at its false default so a message longer than one line wraps instead of truncating with
        // "..." - the two-line-tall Size above is sized for that wrapped case.
        _warningBanner = new WarningBanner
        {
            Location = new Point(222, 126),
            Size = new Size(362, 40),
            BackColor = Color.FromArgb(80, AppTheme.Warning),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
            Visible = false,
        };

        var selectWindowButton = new DarkButton { Text = "Select Window", Location = new Point(222, 170), Width = 128 };
        selectWindowButton.Click += (_, _) => SelectWindowForEntry();
        _removeEntryButton = new DarkButton { Text = "Remove", Location = new Point(358, 170), Width = 100, Enabled = false };
        _removeEntryButton.Click += (_, _) => RemoveSelectedEntry();

        var entrySeparator = new Panel { Location = new Point(222, 204), Width = 362, Height = 1, BackColor = AppTheme.Border };

        var pathLabel = new Label { Text = "Program Path", Location = new Point(222, 216), AutoSize = true };
        _programPathBox = CreateTextBox(new Rectangle(222, 236, 290, 24));
        _programPathBox.ReadOnly = true; // set via Browse - a hand-typed path skips ShortcutResolver's .lnk handling for no benefit
        var browseButton = new DarkButton { Text = "...", Location = new Point(518, 236), Width = 26, Height = 24 };
        browseButton.Click += (_, _) => BrowseForProgram();

        var monitorLabel = new Label { Text = "Monitor", Location = new Point(222, 268), AutoSize = true };
        _monitorCombo = new ComboButton { Location = new Point(222, 288), Width = 170, Height = 24 };
        _monitorCombo.SetItems(_screens.Select(DescribeScreen).ToList(), 0);
        _monitorCombo.SelectedIndexChanged += _ => CommitEntryFields();

        var placementLabel = new Label { Text = "Placement", Location = new Point(414, 268), AutoSize = true };
        _placementCombo = new ComboButton { Location = new Point(414, 288), Width = 170, Height = 24 };
        _placementCombo.SetItems(Enum.GetValues<LayoutPlacement>().Select(DescribePlacement).ToList(), 0);
        _placementCombo.SelectedIndexChanged += _ => CommitEntryFields();

        // Only shown for an entry whose program resolves to a recognized browser (see
        // WindowPlacer.IsBrowserProgram) - a plain .exe has nothing sensible to do with a URL, so
        // this whole group stays hidden rather than sitting there always-visible but usually
        // meaningless. Multiple URLs are all opened as separate tabs in the same forced new window
        // (see WindowPlacer.BuildNewWindowArgs) - one row per URL, typed into _urlInputBox and added
        // with "Add" (or Enter), removed with the same per-row "x" click the Programs list above
        // uses. Occupies the same rectangle as the Commands group below - an entry is never both a
        // browser and a terminal, so RefreshTypeSpecificFieldVisibility only ever shows one at once.
        _urlLabel = new Label { Text = "URLs to open (browser only)", Location = new Point(222, 320), AutoSize = true, Visible = false };
        _urlList = CreateList(new Rectangle(222, 340, 362, 70), removable: true);
        _urlList.Visible = false;
        _urlList.MouseDown += (_, e) =>
        {
            if (TryGetRemoveClickIndex(_urlList, e.Location, out var index))
                RemoveUrlAt(index);
        };

        _urlInputBox = CreateTextBox(new Rectangle(222, 416, 290, 24));
        _urlInputBox.Visible = false;
        _urlInputBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            CommitUrlInput();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };

        _addUrlButton = new DarkButton { Text = "Add", Location = new Point(518, 416), Width = 66, Height = 24, Visible = false };
        _addUrlButton.Click += (_, _) => CommitUrlInput();

        // Same shape as the URL group above (see WindowPlacer.BuildTerminalCommandArgs), shown only
        // for an entry whose program resolves to a recognized terminal (WindowPlacer.IsTerminalProgram)
        // - one row per command, run in order and left open afterward. A blank row (added via "Tab"
        // rather than typed - see AddTabSeparator) splits the commands around it into separate
        // WindowsTerminal.exe tabs; for a directly-captured cmd.exe/powershell.exe/pwsh.exe console
        // (no tab concept of its own) it's ignored and every row just runs in the one sequence.
        _commandLabel = new Label { Text = "Commands to run (terminal only)", Location = new Point(222, 320), AutoSize = true, Visible = false };
        _commandList = CreateList(new Rectangle(222, 340, 362, 70), removable: true, drawItem: DrawCommandListItem);
        _commandList.Visible = false;
        _commandList.MouseDown += (_, e) =>
        {
            if (TryGetRemoveClickIndex(_commandList, e.Location, out var index))
                RemoveCommandAt(index);
        };

        _commandInputBox = CreateTextBox(new Rectangle(222, 416, 214, 24));
        _commandInputBox.Visible = false;
        _commandInputBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            CommitCommandInput();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };

        _addCommandButton = new DarkButton { Text = "Add", Location = new Point(442, 416), Width = 66, Height = 24, Visible = false };
        _addCommandButton.Click += (_, _) => CommitCommandInput();

        // Only meaningful for a WindowsTerminal.exe entry (see the Commands comment above) but shown
        // for any terminal entry regardless - a directly-captured console just ignores the separator
        // it adds, same as it ignores one typed in by hand on old data, rather than hiding a button
        // whose effect quietly depends on the Shell picker's own visibility.
        _addTabButton = new DarkButton { Text = "New Tab", Location = new Point(514, 416), Width = 70, Height = 24, Visible = false };
        _addTabButton.Click += (_, _) => AddTabSeparator();

        // Only shown for an entry captured as WindowsTerminal.exe, alongside the Commands group
        // above (never for a directly-captured cmd.exe/powershell.exe/pwsh.exe entry, whose shell
        // is already implied by its program path) - see TerminalShellOptions/LayoutEntry.TerminalShellExe
        // for why WindowsTerminal.exe alone can't say which shell Command's lines should run in.
        _terminalShellLabel = new Label { Text = "Shell:", Location = new Point(426, 322), AutoSize = true, Visible = false };
        _terminalShellCombo = new ComboButton { Location = new Point(466, 316), Width = 118, Height = 24, Visible = false };
        _terminalShellCombo.SetItems(TerminalShellOptions.Select(o => o.Display).ToList(), 0);
        _terminalShellCombo.SelectedIndexChanged += _ => CommitTerminalShell();

        // Independent of Placement, not shown/hidden with the URL/Command controls the way those are - see
        // LayoutEntry.Minimized for why this stacks on top of whatever Placement already resolved
        // to rather than replacing it.
        _minimizedCheck = new DarkCheckBox { Text = "Start minimized", Location = new Point(222, 444), AutoSize = true, ForeColor = AppTheme.Text };
        _minimizedCheck.CheckedChanged += (_, _) => CommitEntryFields();
        WireDisabledColor(_minimizedCheck);

        SetEntryFieldsEnabled(false);

        _runButton = new DarkButton { Text = "Run", Location = new Point(12, 486), Width = 100, Enabled = false };
        _runButton.Click += async (_, _) => await RunSelectedProfile();

        var closeButton = new DarkButton { Text = "Close", Location = new Point(484, 486), Width = 100 };
        closeButton.Click += (_, _) => Close();

        foreach (var button in new[]
        {
            newProfileButton, _deleteProfileButton, selectWindowButton, _removeEntryButton, browseButton,
            _addUrlButton, _addCommandButton, _addTabButton, _runButton, closeButton,
        })
            StyleButton(button);

        Controls.AddRange(new Control[]
        {
            profilesLabel, _profileList, newProfileButton, _deleteProfileButton, nameLabel, _profileNameBox,
            separator, entriesLabel, _entryList, _warningBanner, selectWindowButton, _removeEntryButton, entrySeparator,
            pathLabel, _programPathBox, browseButton, monitorLabel, _monitorCombo, placementLabel, _placementCombo,
            _urlLabel, _urlList, _urlInputBox, _addUrlButton,
            _commandLabel, _commandList, _commandInputBox, _addCommandButton, _addTabButton,
            _terminalShellLabel, _terminalShellCombo,
            _minimizedCheck, _runButton, closeButton,
        });

        RefreshProfileList();
        if (initialProfileId is { } id)
            SelectByIdIfPresent(id);
    }

    /// <summary>Lets an external caller (TrayApplicationContext, after a fresh "Save Current
    /// Layout" capture) jump an already-open editor to a specific profile - the constructor's own
    /// initialProfileId only helps for a brand new instance.</summary>
    public void SelectProfileById(Guid id)
    {
        RefreshProfileList();
        SelectByIdIfPresent(id);
    }

    private void SelectByIdIfPresent(Guid id)
    {
        var index = _manager.Profiles.ToList().FindIndex(p => p.Id == id);
        if (index >= 0)
            _profileList.SelectedIndex = index;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var useDarkMode = 1;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
    }

    private void OnLaunchFailed(object? sender, (string ProfileName, IReadOnlyList<string> FailedPrograms) e)
    {
        _profileList.Invalidate();
        // Safe to call regardless of which profile just ran - RefreshWarningBanner only ever reads
        // the currently selected profile's own GetLaunchError, so a run triggered elsewhere (the
        // Layout Launcher widget, say, while this editor happens to be open) only touches the banner
        // if that's the profile actually selected right now.
        RefreshWarningBanner();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _manager.LaunchFailed -= OnLaunchFailed;
        base.Dispose(disposing);
    }

    /// <summary>removable draws a small "x" at the right edge of every row (see
    /// DrawRemovableListItem/TryGetRemoveClickIndex) - only the Programs and URLs lists opt into
    /// it; the Layouts (profile) list keeps its plain Delete-button-only removal. drawItem overrides
    /// both defaults - only the Layouts list needs this, for DrawProfileListItem's caution icon.</summary>
    private static ListBox CreateList(Rectangle bounds, bool removable = false, DrawItemEventHandler? drawItem = null)
    {
        var list = new ListBox
        {
            Location = bounds.Location,
            Size = bounds.Size,
            BackColor = AppTheme.Field,
            ForeColor = AppTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 20,
        };
        list.DrawItem += drawItem ?? (removable ? DrawRemovableListItem : DrawListItem);

        // Same theming battle CreateTextBox already fought (see its own comment) - a themed native
        // ListBox paints its own blue focus/selection border regardless of BackColor/ForeColor,
        // clashing with this app's dark theme instead of matching it. Handle isn't created yet here
        // (control isn't parented), hence HandleCreated rather than calling SetWindowTheme directly.
        list.HandleCreated += (_, _) => NativeMethods.SetWindowTheme(list.Handle, "", "");
        return list;
    }

    /// <summary>Same dark fill + hover-colored selection + WhiteSmoke text every other owner-drawn
    /// row in this app uses (DropdownMenu.DrawRow, TrayMenuRenderer.OnRenderMenuItemBackground) -
    /// a plain (non-owner-drawn) ListBox instead uses SystemColors.Highlight for its selection,
    /// which doesn't match this theme at all.</summary>
    private static void DrawListItem(object? sender, DrawItemEventArgs e)
    {
        var list = (ListBox)sender!;
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (var background = new SolidBrush(selected ? AppTheme.Hover : AppTheme.Field))
            e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index >= 0 && e.Index < list.Items.Count)
        {
            TextRenderer.DrawText(e.Graphics, list.Items[e.Index]!.ToString(), list.Font, e.Bounds, AppTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Same as DrawListItem, but reserves a strip at the row's left edge for a small caution
    /// glyph on any profile with at least one entry whose saved monitor isn't currently connected
    /// (see IsMonitorMissing), or whose last run left at least one program un-launched (see
    /// LayoutManager.GetLaunchError - the same glyph Layout Launcher's own row uses, so a profile
    /// reads as "has a problem" the same way in both places) - an instance method (needs _manager),
    /// unlike every other draw handler here, since both checks read live profile/screen/run-result
    /// data rather than anything baked into the list's own Items strings.</summary>
    private void DrawProfileListItem(object? sender, DrawItemEventArgs e)
    {
        var list = (ListBox)sender!;
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (var background = new SolidBrush(selected ? AppTheme.Hover : AppTheme.Field))
            e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index < 0 || e.Index >= list.Items.Count || e.Index >= _manager.Profiles.Count)
            return;

        var profile = _manager.Profiles[e.Index];
        var textRect = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height);
        if (ProfileHasMissingMonitor(profile) || _manager.GetLaunchError(profile.Id) is not null)
        {
            var iconRect = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, 16, e.Bounds.Height);
            WarningIcon.Paint(e.Graphics, iconRect, AppTheme.Text);
            textRect = new Rectangle(e.Bounds.X + 22, e.Bounds.Y, e.Bounds.Width - 22, e.Bounds.Height);
        }

        TextRenderer.DrawText(e.Graphics, list.Items[e.Index]!.ToString(), list.Font, textRect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Same as DrawListItem, but reserves a fixed strip at the row's right edge for an "x"
    /// glyph - GetRemoveGlyphRect defines exactly that strip, shared with TryGetRemoveClickIndex so
    /// the drawn glyph and the clickable area are always the same rectangle.</summary>
    private static void DrawRemovableListItem(object? sender, DrawItemEventArgs e)
    {
        var list = (ListBox)sender!;
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (var background = new SolidBrush(selected ? AppTheme.Hover : AppTheme.Field))
            e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index < 0 || e.Index >= list.Items.Count)
            return;

        var glyphRect = GetRemoveGlyphRect(e.Bounds);
        var textRect = new Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width - glyphRect.Width, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, list.Items[e.Index]!.ToString(), list.Font, textRect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, "x", list.Font, glyphRect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
    }

    /// <summary>Same as DrawRemovableListItem, but a blank row (see AddTabSeparator) renders as a
    /// dim centered "New Tab" caption instead of an empty clickable strip, so a tab break reads as a
    /// deliberate break rather than a stray empty row - still keeps the same removable "x" glyph as
    /// any other row, since TryGetRemoveClickIndex hit-tests the rect, not the row's text.</summary>
    private static void DrawCommandListItem(object? sender, DrawItemEventArgs e)
    {
        var list = (ListBox)sender!;
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (var background = new SolidBrush(selected ? AppTheme.Hover : AppTheme.Field))
            e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index < 0 || e.Index >= list.Items.Count)
            return;

        var glyphRect = GetRemoveGlyphRect(e.Bounds);
        var textRect = new Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width - glyphRect.Width, e.Bounds.Height);
        var text = list.Items[e.Index]!.ToString()!;
        if (text.Length == 0)
        {
            TextRenderer.DrawText(e.Graphics, "── New Tab ──", list.Font, textRect, AppTheme.DisabledText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, text, list.Font, textRect, AppTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        TextRenderer.DrawText(e.Graphics, "x", list.Font, glyphRect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
    }

    private static Rectangle GetRemoveGlyphRect(Rectangle itemBounds) =>
        new(itemBounds.Right - 20, itemBounds.Top, 20, itemBounds.Height);

    /// <summary>Hit-tests a removable list click against the "x" glyph DrawRemovableListItem drew
    /// for whichever row is under the cursor - shared by the Programs and URLs lists' own MouseDown
    /// wiring, both of which remove a row on a direct click rather than requiring select-then-press
    /// a separate Remove button.</summary>
    private static bool TryGetRemoveClickIndex(ListBox list, Point location, out int index)
    {
        index = list.IndexFromPoint(location);
        if (index < 0 || index >= list.Items.Count)
            return false;

        return GetRemoveGlyphRect(list.GetItemRectangle(index)).Contains(location);
    }

    private static TextBox CreateTextBox(Rectangle bounds)
    {
        var box = new TextBox
        {
            Location = bounds.Location,
            Size = bounds.Size,
            BackColor = AppTheme.Field,
            ForeColor = AppTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };

        // A themed native Edit control paints its own disabled-state text color once Enabled goes
        // false, ignoring whatever ForeColor this TextBox was given - same theming battle
        // DropdownMenu's own tooltip fought (see its class comment), same fix: opt this control out
        // of visual-style theming entirely so WireDisabledColor's ForeColor actually sticks instead
        // of being silently overridden. Handle isn't created yet at this point (control isn't
        // parented), hence HandleCreated rather than calling SetWindowTheme directly here.
        box.HandleCreated += (_, _) => NativeMethods.SetWindowTheme(box.Handle, "", "");
        WireDisabledColor(box);
        return box;
    }

    private static void StyleButton(Button button)
    {
        AppTheme.StyleButton(button);
        // Fixed BackColor, unlike AppTheme.StyleButton's own tinted-widget callers (LayoutLauncherWidget)
        // - this editor has no per-instance tint to track, so there's nothing dynamic to leave this
        // for.
        button.BackColor = AppTheme.Field;
        WireDisabledColor(button);
    }

    /// <summary>Keeps a control's own ForeColor at AppTheme.DisabledText instead of whatever
    /// system-drawn disabled color WinForms/uxtheme would otherwise substitute in (see
    /// CreateTextBox's own comment) - every place in this editor that toggles some field's Enabled
    /// to mean "not applicable to the current selection" (SetEntryFieldsEnabled, the profile/entry
    /// action buttons) goes through a control wired here rather than needing its own explicit
    /// color-swap logic.</summary>
    private static void WireDisabledColor(Control control)
    {
        control.ForeColor = control.Enabled ? AppTheme.Text : AppTheme.DisabledText;
        control.EnabledChanged += (_, _) => control.ForeColor = control.Enabled ? AppTheme.Text : AppTheme.DisabledText;
    }

    private void RefreshProfileList()
    {
        var previouslySelectedId = _selectedProfile?.Id;

        _isPopulating = true;
        _profileList.Items.Clear();
        foreach (var profile in _manager.Profiles)
            _profileList.Items.Add(profile.Name);
        _isPopulating = false;

        var index = previouslySelectedId is { } id ? _manager.Profiles.ToList().FindIndex(p => p.Id == id) : -1;
        _profileList.SelectedIndex = index >= 0 ? index : (_manager.Profiles.Count > 0 ? 0 : -1);
        if (_profileList.SelectedIndex < 0)
            SelectProfile(-1); // nothing left to select - clear dependent state explicitly
    }

    private void SelectProfile(int index)
    {
        if (_isPopulating)
            return;

        _selectedProfile = index >= 0 && index < _manager.Profiles.Count ? _manager.Profiles[index] : null;
        _deleteProfileButton.Enabled = _selectedProfile is not null;
        _runButton.Enabled = _selectedProfile is { Entries.Count: > 0 };

        _isPopulating = true;
        _profileNameBox.Text = _selectedProfile?.Name ?? string.Empty;
        _isPopulating = false;

        RefreshEntryList();
        RefreshWarningBanner();
    }

    /// <summary>True for an entry whose saved TargetMonitor was actually set (not just defaulting
    /// to primary - see LayoutEntry.TargetMonitor) but no longer matches any currently connected
    /// display's DeviceName - e.g. a monitor that's since been unplugged, or a transient
    /// virtual/RDP display that was attached when the layout was captured. Shared by
    /// DrawProfileListItem's caution icon, DescribeEntry's Programs-list row text, SelectEntry's
    /// Monitor combo, and RefreshWarningBanner - all four need to agree on exactly the same
    /// definition of "missing" or they'd tell the user conflicting things about the same entry.</summary>
    private bool IsMonitorMissing(LayoutEntry entry) =>
        !string.IsNullOrEmpty(entry.TargetMonitor) && _screens.All(s => s.DeviceName != entry.TargetMonitor);

    private bool ProfileHasMissingMonitor(LayoutProfile profile) => profile.Entries.Any(IsMonitorMissing);

    /// <summary>Keeps _warningBanner in sync with the selected profile's current problems - missing-
    /// monitor entries (see IsMonitorMissing) and/or the last run's launch failures (see
    /// LayoutManager.GetLaunchError) - called after anything that could change either: selecting a
    /// different profile, committing an entry's monitor fix, adding/removing an entry, or a run
    /// finishing (see RunSelectedProfile/OnLaunchFailed). Both show at once, one line each, rather
    /// than one hiding the other.</summary>
    private void RefreshWarningBanner()
    {
        if (_selectedProfile is not { } profile)
        {
            _warningBanner.Visible = false;
            return;
        }

        var messages = new List<string>();

        var missingCount = profile.Entries.Count(IsMonitorMissing);
        if (missingCount > 0)
        {
            messages.Add(missingCount == 1
                ? "1 program is set to a monitor that's no longer connected. Pick its correct monitor below."
                : $"{missingCount} programs are set to a monitor that's no longer connected. Pick their correct monitor below.");
        }

        if (_manager.GetLaunchError(profile.Id) is { Count: > 0 } failedPrograms)
            messages.Add($"Didn't launch on the last run: {string.Join(", ", failedPrograms)}.");

        _warningBanner.Visible = messages.Count > 0;
        _warningBanner.Text = string.Join("\n", messages);
    }

    private void AddProfile()
    {
        var profile = _manager.CreateLayout($"Layout {_manager.Profiles.Count + 1}");
        RefreshProfileList();
        _profileList.SelectedIndex = _manager.Profiles.ToList().FindIndex(p => p.Id == profile.Id);
    }

    private void DeleteSelectedProfile()
    {
        if (_selectedProfile is not { } profile)
            return;

        // Same "Delete Fence" confirmation FenceForm.ConfirmDelete shows - unlike removing a single
        // program from a profile (see RemoveSelectedEntry, which doesn't confirm), losing an entire
        // saved layout is enough to ask first.
        var result = MessageBox.Show(this, $"Delete \"{profile.Name}\"?", "Delete Layout",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
            return;

        _manager.DeleteLayout(profile.Id);
        _selectedProfile = null;
        RefreshProfileList();
    }

    private void CommitProfileName()
    {
        if (_isPopulating || _selectedProfile is not { } profile)
            return;

        var name = _profileNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || name == profile.Name)
            return;

        profile.Name = name;
        _manager.UpdateLayout(profile);
        RefreshProfileList();
    }

    private void RefreshEntryList()
    {
        _isPopulating = true;
        _entryList.Items.Clear();
        foreach (var entry in _selectedProfile?.Entries ?? Enumerable.Empty<LayoutEntry>())
            _entryList.Items.Add(DescribeEntry(entry));
        _isPopulating = false;

        _entryList.SelectedIndex = -1;
        SelectEntry(-1);
    }

    private void SelectEntry(int index)
    {
        if (_isPopulating)
            return;

        _selectedEntry = _selectedProfile is not null && index >= 0 && index < _selectedProfile.Entries.Count
            ? _selectedProfile.Entries[index]
            : null;
        _removeEntryButton.Enabled = _selectedEntry is not null;
        SetEntryFieldsEnabled(_selectedEntry is not null);

        if (_selectedEntry is not { } entry)
            return;

        _isPopulating = true;
        _programPathBox.Text = entry.ProgramPath;
        var monitorIndex = _screens.FindIndex(s => s.DeviceName == entry.TargetMonitor);
        var monitorItems = _screens.Select(DescribeScreen).ToList();
        if (IsMonitorMissing(entry))
        {
            // entry.TargetMonitor was set (not just defaulting to primary) but doesn't match any
            // currently connected screen - e.g. saved while a since-disconnected monitor, or a
            // transient virtual/RDP display, was attached. Shown as its own selectable-but-inert
            // item (see the SelectedIndex-out-of-_screens-range guard in CommitEntryFields) rather
            // than silently defaulting the combo to Screen 1, which would look like Screen 1 was
            // actually chosen and (if the user then touched anything else on this entry) would
            // silently overwrite entry.TargetMonitor with that wrong guess.
            monitorItems.Add(MonitorNotConnectedLabel);
            monitorIndex = _screens.Count;
        }

        _monitorCombo.SetItems(monitorItems, Math.Max(monitorIndex, 0));
        _placementCombo.SetItems(Enum.GetValues<LayoutPlacement>().Select(DescribePlacement).ToList(), (int)entry.Placement);
        _minimizedCheck.Checked = entry.Minimized;
        _isPopulating = false;

        RefreshUrlList(entry);
        RefreshCommandList(entry);
        RefreshTypeSpecificFieldVisibility(entry);
    }

    private void SetEntryFieldsEnabled(bool enabled)
    {
        _programPathBox.Enabled = enabled;
        _monitorCombo.Enabled = enabled;
        _placementCombo.Enabled = enabled;
        _urlList.Enabled = enabled;
        _urlInputBox.Enabled = enabled;
        _addUrlButton.Enabled = enabled;
        _commandList.Enabled = enabled;
        _commandInputBox.Enabled = enabled;
        _addCommandButton.Enabled = enabled;
        _addTabButton.Enabled = enabled;
        _terminalShellCombo.Enabled = enabled;
        _minimizedCheck.Enabled = enabled;
        if (!enabled)
        {
            _urlLabel.Visible = false;
            _urlList.Visible = false;
            _urlInputBox.Visible = false;
            _addUrlButton.Visible = false;
            _commandLabel.Visible = false;
            _commandList.Visible = false;
            _commandInputBox.Visible = false;
            _addCommandButton.Visible = false;
            _addTabButton.Visible = false;
            _terminalShellLabel.Visible = false;
            _terminalShellCombo.Visible = false;
        }
    }

    /// <summary>Shows/hides the URL controls (browser) or Command controls (terminal) based on
    /// entry's current program - called after selecting an entry and after BrowseForProgram changes
    /// its program path, since the two can disagree (e.g. switching a browser entry's path to a
    /// non-browser .exe). Never both at once - a program can't resolve as both a recognized browser
    /// and a recognized terminal.</summary>
    private void RefreshTypeSpecificFieldVisibility(LayoutEntry entry)
    {
        var isBrowser = WindowPlacer.IsBrowserProgram(entry.ProgramPath);
        _urlLabel.Visible = isBrowser;
        _urlList.Visible = isBrowser;
        _urlInputBox.Visible = isBrowser;
        _addUrlButton.Visible = isBrowser;

        var isTerminal = WindowPlacer.IsTerminalProgram(entry.ProgramPath);
        _commandLabel.Visible = isTerminal;
        _commandList.Visible = isTerminal;
        _commandInputBox.Visible = isTerminal;
        _addCommandButton.Visible = isTerminal;
        _addTabButton.Visible = isTerminal;

        var isWindowsTerminal = isTerminal && WindowPlacer.IsWindowsTerminalProgram(entry.ProgramPath);
        _terminalShellLabel.Visible = isWindowsTerminal;
        _terminalShellCombo.Visible = isWindowsTerminal;
        if (isWindowsTerminal)
        {
            var shellIndex = Array.FindIndex(TerminalShellOptions,
                o => string.Equals(o.ExeName, entry.TerminalShellExe, StringComparison.OrdinalIgnoreCase));
            _isPopulating = true;
            _terminalShellCombo.SetItems(TerminalShellOptions.Select(o => o.Display).ToList(), Math.Max(shellIndex, 0));
            _isPopulating = false;
        }
    }

    /// <summary>Repopulates _urlList from entry.Url's raw newline-separated text (see
    /// WindowPlacer.BuildNewWindowArgs) - called on selecting an entry, mirroring how
    /// RefreshEntryList repopulates _entryList from the profile's Entries.</summary>
    private void RefreshUrlList(LayoutEntry entry)
    {
        _isPopulating = true;
        _urlList.Items.Clear();
        foreach (var url in SplitLines(entry.Url))
            _urlList.Items.Add(url);
        _isPopulating = false;
    }

    /// <summary>Same as RefreshUrlList, for entry.Command (see WindowPlacer.BuildTerminalCommandArgs)
    /// instead of entry.Url - uses SplitCommandLines rather than SplitLines since a blank row here is
    /// meaningful (a tab separator, see AddTabSeparator) rather than noise to discard.</summary>
    private void RefreshCommandList(LayoutEntry entry)
    {
        _isPopulating = true;
        _commandList.Items.Clear();
        foreach (var command in SplitCommandLines(entry.Command))
            _commandList.Items.Add(command);
        _isPopulating = false;
    }

    private static string[] SplitLines(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? Array.Empty<string>() : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Same as SplitLines, but keeps a single blank row for each run of blank lines instead
    /// of discarding them - each one is a tab separator AddTabSeparator put there, not incidental
    /// whitespace. Collapses runs of several into one and drops any at the very start/end (both would
    /// otherwise round-trip back in as a leading/trailing separator that AddCommand's own "don't add
    /// a redundant one" check would then have to account for on the next add).</summary>
    private static string[] SplitCommandLines(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 && (result.Count == 0 || result[^1].Length == 0))
                continue;

            result.Add(line);
        }

        if (result.Count > 0 && result[^1].Length == 0)
            result.RemoveAt(result.Count - 1);

        return result.ToArray();
    }

    /// <summary>"Select Window" - shows a full-screen click-catcher (WindowPickerOverlay) so the user
    /// can point at any window on screen instead of hunting down its .exe by hand via Browse. Captures
    /// the clicked window's current monitor/position too (same rules "Save Current Layout" uses for
    /// each window it captures - see WindowPlacer.CaptureWindow), so the new entry starts out placed
    /// where that window already was rather than defaulting blank.</summary>
    private void SelectWindowForEntry()
    {
        if (_selectedProfile is not { } profile)
            return;

        var overlay = new WindowPickerOverlay();
        overlay.WindowPicked += entry =>
        {
            profile.Entries.Add(entry);
            _manager.UpdateLayout(profile);
            RefreshEntryList();
            RefreshWarningBanner();
            _entryList.SelectedIndex = profile.Entries.Count - 1;
            _runButton.Enabled = true;
        };
        overlay.FormClosed += (_, _) => overlay.Dispose();
        overlay.Show();
    }

    private void RemoveSelectedEntry() => RemoveEntryAt(_entryList.SelectedIndex);

    private void RemoveEntryAt(int index)
    {
        if (_selectedProfile is not { } profile || index < 0 || index >= profile.Entries.Count)
            return;

        profile.Entries.RemoveAt(index);
        _manager.UpdateLayout(profile);
        RefreshEntryList();
        RefreshWarningBanner();
        _profileList.Invalidate();
        _runButton.Enabled = profile.Entries.Count > 0;
    }

    private void BrowseForProgram()
    {
        if (_selectedEntry is null)
            return;

        using var dialog = new OpenFileDialog
        {
            Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
            Title = "Choose a program",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _selectedEntry.ProgramPath = dialog.FileName;
        _programPathBox.Text = dialog.FileName;
        RefreshTypeSpecificFieldVisibility(_selectedEntry);
        CommitEntryFields();
    }

    private void CommitUrlInput()
    {
        var url = _urlInputBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
            return;

        AddUrl(url);
        _urlInputBox.Clear();
    }

    private void AddUrl(string url)
    {
        if (_selectedEntry is null || string.IsNullOrWhiteSpace(url))
            return;

        _urlList.Items.Add(url.Trim());
        SaveUrlList();
    }

    private void RemoveUrlAt(int index)
    {
        if (_selectedEntry is null || index < 0 || index >= _urlList.Items.Count)
            return;

        _urlList.Items.RemoveAt(index);
        SaveUrlList();
    }

    /// <summary>Writes _urlList's current rows back into _selectedEntry.Url as newline-separated
    /// text (see LayoutEntry.Url) and persists - called after every add/remove rather than requiring
    /// a separate commit step, the same immediate-persist behavior the Programs list already has.</summary>
    private void SaveUrlList()
    {
        if (_selectedProfile is not { } profile || _selectedEntry is not { } entry)
            return;

        entry.Url = _urlList.Items.Count == 0 ? null : string.Join('\n', _urlList.Items.Cast<string>());
        _manager.UpdateLayout(profile);
    }

    private void CommitCommandInput()
    {
        var command = _commandInputBox.Text.Trim();
        if (string.IsNullOrEmpty(command))
            return;

        AddCommand(command);
        _commandInputBox.Clear();
    }

    private void AddCommand(string command)
    {
        if (_selectedEntry is null || string.IsNullOrWhiteSpace(command))
            return;

        _commandList.Items.Add(command.Trim());
        SaveCommandList();
    }

    private void RemoveCommandAt(int index)
    {
        if (_selectedEntry is null || index < 0 || index >= _commandList.Items.Count)
            return;

        _commandList.Items.RemoveAt(index);
        SaveCommandList();
    }

    /// <summary>Adds a blank row to _commandList - WindowPlacer.SplitIntoTabs reads a blank row as
    /// "start a new WindowsTerminal.exe tab here" (see BuildTerminalCommandArgs). Refuses on an empty
    /// list (nothing yet to split into a first tab) or right after another separator (two in a row
    /// would mean an empty tab in between, which SplitIntoTabs would just skip anyway - no point
    /// letting the row list show a break that does nothing).</summary>
    private void AddTabSeparator()
    {
        if (_selectedEntry is null || _commandList.Items.Count == 0)
            return;

        if (_commandList.Items[^1] is string last && last.Length == 0)
            return;

        _commandList.Items.Add(string.Empty);
        SaveCommandList();
    }

    /// <summary>Same as SaveUrlList, for entry.Command instead of entry.Url.</summary>
    private void SaveCommandList()
    {
        if (_selectedProfile is not { } profile || _selectedEntry is not { } entry)
            return;

        entry.Command = _commandList.Items.Count == 0 ? null : string.Join('\n', _commandList.Items.Cast<string>());
        _manager.UpdateLayout(profile);
    }

    /// <summary>Writes _terminalShellCombo's current selection back into _selectedEntry.TerminalShellExe
    /// and persists - same immediate-persist shape as CommitEntryFields, kept separate since this one
    /// only ever applies to a WindowsTerminal.exe entry (see RefreshTypeSpecificFieldVisibility).</summary>
    private void CommitTerminalShell()
    {
        if (_isPopulating || _selectedProfile is not { } profile || _selectedEntry is not { } entry)
            return;

        var index = Math.Clamp(_terminalShellCombo.SelectedIndex, 0, TerminalShellOptions.Length - 1);
        entry.TerminalShellExe = TerminalShellOptions[index].ExeName;
        _manager.UpdateLayout(profile);
    }

    /// <summary>Writes the two combo pickers' current selections back into _selectedEntry and
    /// persists - called from both combos' own SelectedIndexChanged and from BrowseForProgram,
    /// since all three edit the same entry object.</summary>
    private void CommitEntryFields()
    {
        if (_isPopulating || _selectedProfile is not { } profile || _selectedEntry is not { } entry)
            return;

        // Only a real screen selection overwrites TargetMonitor - the "(monitor not connected)"
        // placeholder item SelectEntry appends sits one past the end of _screens, and re-picking
        // it (it's still a clickable row, just not backed by a real Screen) shouldn't silently
        // stomp the entry's original saved device name with whatever Math.Clamp fell back to.
        if (_monitorCombo.SelectedIndex < _screens.Count)
            entry.TargetMonitor = _screens[Math.Clamp(_monitorCombo.SelectedIndex, 0, _screens.Count - 1)].DeviceName;
        entry.Placement = (LayoutPlacement)Math.Clamp(_placementCombo.SelectedIndex, 0, Enum.GetValues<LayoutPlacement>().Length - 1);
        entry.Minimized = _minimizedCheck.Checked;
        _manager.UpdateLayout(profile);

        var index = _entryList.SelectedIndex;
        _isPopulating = true;
        _entryList.Items[index] = DescribeEntry(entry);
        _isPopulating = false;

        RefreshWarningBanner();
        _profileList.Invalidate();
    }

    private async Task RunSelectedProfile()
    {
        if (_selectedProfile is not { } profile)
            return;

        _runButton.Enabled = false;
        try
        {
            await _manager.RunLayoutAsync(profile.Id);
        }
        finally
        {
            _runButton.Enabled = true;
            // Whether this run left a launch error behind (see LayoutManager.GetLaunchError) or
            // cleared one from a previous attempt, DrawProfileListItem's own caution glyph and
            // _warningBanner both need to catch up - nothing else after a Run click would otherwise
            // trigger either (OnLaunchFailed only fires on failure, not on a clean run that clears a
            // previous one).
            _profileList.Invalidate();
            RefreshWarningBanner();
        }
    }

    private string DescribeEntry(LayoutEntry entry)
    {
        var program = string.IsNullOrEmpty(entry.ProgramPath) ? "(no program set)" : Path.GetFileName(entry.ProgramPath);
        var monitorIndex = _screens.FindIndex(s => s.DeviceName == entry.TargetMonitor);
        var monitor = monitorIndex >= 0 ? $"Screen {monitorIndex + 1}"
            : IsMonitorMissing(entry) ? MonitorNotConnectedLabel : "Screen 1";
        var minimizedSuffix = entry.Minimized ? " (minimized)" : string.Empty;
        return $"{program} — {monitor} — {DescribePlacement(entry.Placement)}{minimizedSuffix}";
    }

    private static string DescribeScreen(Screen screen)
    {
        var index = Array.IndexOf(Screen.AllScreens, screen);
        var primary = screen.Primary ? ", primary" : string.Empty;
        return $"Screen {index + 1} ({screen.Bounds.Width}x{screen.Bounds.Height}{primary})";
    }

    private static string DescribePlacement(LayoutPlacement placement) => placement switch
    {
        LayoutPlacement.LeftHalf => "Left Half",
        LayoutPlacement.RightHalf => "Right Half",
        LayoutPlacement.TopHalf => "Top Half",
        LayoutPlacement.BottomHalf => "Bottom Half",
        LayoutPlacement.TopLeftQuarter => "Top-Left Quarter",
        LayoutPlacement.TopRightQuarter => "Top-Right Quarter",
        LayoutPlacement.BottomLeftQuarter => "Bottom-Left Quarter",
        LayoutPlacement.BottomRightQuarter => "Bottom-Right Quarter",
        LayoutPlacement.Maximized => "Maximized",
        LayoutPlacement.Custom => "Custom (captured)",
        _ => placement.ToString(),
    };

    /// <summary>Same reasoning and same only-override-the-disabled-case shape as the shared
    /// DesktopTool.UI.DarkButton (used for every button in this editor) - a
    /// FlatStyle.Standard CheckBox (the default, used for _minimizedCheck) draws its disabled state
    /// through native visual-style theming, which - like the native Edit control CreateTextBox
    /// fights with SetWindowTheme - ignores ForeColor entirely for that state. Rolling a themed
    /// SetWindowTheme opt-out for a CheckBox is unreliable (the box glyph itself is theme-drawn, not
    /// just the text), so this draws the whole control by hand instead, only when disabled.</summary>
    private sealed class DarkCheckBox : CheckBox
    {
        private const int BoxSize = 13;

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled)
            {
                base.OnPaint(e);
                return;
            }

            // Unlike base.OnPaint (which paints a transparent background by compositing against
            // the parent - the same SupportsTransparentBackColor behavior Label gets for free, see
            // _warningBanner's own comment), this override draws nothing at all unless told
            // to - skip this fill and whatever was rendered here last (another control's text,
            // stale double-buffer content) stays showing through underneath the glyph/text drawn
            // below instead of being cleared first.
            using (var background = new SolidBrush(Parent?.BackColor ?? AppTheme.Body))
                e.Graphics.FillRectangle(background, ClientRectangle);

            var boxRect = new Rectangle(0, (Height - BoxSize) / 2, BoxSize, BoxSize);
            using (var borderPen = new Pen(AppTheme.DisabledText))
                e.Graphics.DrawRectangle(borderPen, boxRect);

            if (Checked)
            {
                using var checkPen = new Pen(AppTheme.DisabledText, 2);
                e.Graphics.DrawLine(checkPen, boxRect.Left + 2, boxRect.Top + 7, boxRect.Left + 5, boxRect.Bottom - 2);
                e.Graphics.DrawLine(checkPen, boxRect.Left + 5, boxRect.Bottom - 2, boxRect.Right - 2, boxRect.Top + 2);
            }

            var textRect = new Rectangle(boxRect.Right + 4, 0, Math.Max(0, Width - boxRect.Right - 4), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect, AppTheme.DisabledText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>The yellow problem-banner Label (_warningBanner) - draws WarningIcon (the same
    /// hand-drawn caution triangle used by DrawProfileListItem and Layout Launcher's own row) at the
    /// left edge before the text, so this reads as the same kind of warning as those rather than
    /// plain unmarked text. Only overrides OnPaint, not OnPaintBackground - the base Label already
    /// blends BackColor's alpha channel against whatever's behind it there (see _warningBanner's own
    /// comment), so this only needs to add the icon and lay the text out in the space left over.</summary>
    private sealed class WarningBanner : Label
    {
        private const int IconSize = 18;

        protected override void OnPaint(PaintEventArgs e)
        {
            var iconRect = new Rectangle(Padding.Left, (Height - IconSize) / 2, IconSize, IconSize);
            WarningIcon.Paint(e.Graphics, iconRect, ForeColor);

            var textRect = new Rectangle(iconRect.Right + 6, 0, Math.Max(0, Width - iconRect.Right - 6 - Padding.Right), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect, ForeColor,
                TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
