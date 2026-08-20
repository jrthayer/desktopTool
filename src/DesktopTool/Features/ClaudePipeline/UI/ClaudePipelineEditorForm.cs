using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.ClaudePipeline.UI;

/// <summary>
/// "Manage Features" window - create/rename/delete PipelineFeatures and add/remove/edit each one's
/// PipelineHookSpec rows (event, matcher, command, args). Same dark-themed plain-Form approach as
/// LayoutEditorForm (AppTheme colors, ComboButton for the Event picker, FlatStyle.Flat buttons) - see
/// that class's own comment for why plain ComboBox/NumericUpDown aren't used here either. Every field
/// edit commits straight into the in-memory PipelineFeature/PipelineHookSpec and persists via
/// ClaudePipelineManager.UpdateFeature on Leave/Enter/selection-change, the same "no separate Save
/// button" convention LayoutEditorForm uses.
/// </summary>
internal sealed class ClaudePipelineEditorForm : Form
{
    private static readonly string[] HookEvents =
    {
        "PreToolUse", "PostToolUse", "UserPromptSubmit", "Stop", "SessionStart", "SessionEnd",
    };

    private readonly ClaudePipelineManager _manager;

    private readonly ListBox _featureList;
    private readonly TextBox _nameBox;
    private readonly TextBox _descriptionBox;
    private readonly Button _deleteFeatureButton;

    private readonly ListBox _hookList;
    private readonly Button _addHookButton;
    private readonly Button _removeHookButton;
    private readonly ComboButton _eventCombo;
    private readonly TextBox _matcherBox;
    private readonly TextBox _commandBox;
    private readonly TextBox _argsBox;

    // Set while this form is itself rewriting a field from the currently-selected feature/hook -
    // without this, populating a field would re-fire its own change handler and write that same
    // value straight back as if the user had just edited it - same guard LayoutEditorForm's own
    // _isPopulating protects against.
    private bool _isPopulating;

    private PipelineFeature? _selectedFeature;
    private PipelineHookSpec? _selectedHook;

    public ClaudePipelineEditorForm(ClaudePipelineManager manager, Guid? initialFeatureId = null)
    {
        _manager = manager;
        _manager.FeaturesChanged += OnFeaturesChanged;

        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Manage Claude Toolbox Features";
        ClientSize = new Size(624, 560);
        MaximizeBox = false;
        MinimizeBox = false;
        Font = AppTheme.Font;
        BackColor = AppTheme.Body;
        ForeColor = AppTheme.Text;

        var featuresLabel = new Label { Text = "Features", Location = new Point(12, 12), AutoSize = true };
        _featureList = CreateList(new Rectangle(12, 32, 190, 300), DrawFeatureListItem);
        _featureList.SelectedIndexChanged += (_, _) => SelectFeature(_featureList.SelectedIndex);

        var newFeatureButton = new DarkButton { Text = "New", Location = new Point(12, 340), Width = 92 };
        newFeatureButton.Click += (_, _) => AddFeature();
        _deleteFeatureButton = new DarkButton { Text = "Delete", Location = new Point(110, 340), Width = 92, Enabled = false };
        _deleteFeatureButton.Click += (_, _) => DeleteSelectedFeature();

        var separator = new Panel { Location = new Point(216, 12), Width = 1, Height = 470, BackColor = AppTheme.Border };

        var nameLabel = new Label { Text = "Name", Location = new Point(232, 12), AutoSize = true };
        _nameBox = CreateTextBox(new Rectangle(232, 32, 322, 24));
        _nameBox.Leave += (_, _) => CommitFeatureFields();
        _nameBox.KeyDown += (_, e) => CommitOnEnter(e, CommitFeatureFields);

        var descriptionLabel = new Label { Text = "Description", Location = new Point(232, 64), AutoSize = true };
        _descriptionBox = CreateTextBox(new Rectangle(232, 84, 322, 24));
        _descriptionBox.Leave += (_, _) => CommitFeatureFields();
        _descriptionBox.KeyDown += (_, e) => CommitOnEnter(e, CommitFeatureFields);

        var hooksLabel = new Label { Text = "Hooks", Location = new Point(232, 122), AutoSize = true };
        _hookList = CreateList(new Rectangle(232, 142, 322, 90), DrawHookListItem);
        _hookList.SelectedIndexChanged += (_, _) => SelectHook(_hookList.SelectedIndex);

        _addHookButton = new DarkButton { Text = "Add Hook", Location = new Point(232, 238), Width = 92, Enabled = false };
        _addHookButton.Click += (_, _) => AddHook();
        _removeHookButton = new DarkButton { Text = "Remove Hook", Location = new Point(330, 238), Width = 106, Enabled = false };
        _removeHookButton.Click += (_, _) => RemoveSelectedHook();

        var hookSeparator = new Panel { Location = new Point(232, 274), Width = 322, Height = 1, BackColor = AppTheme.Border };

        var eventLabel = new Label { Text = "Event", Location = new Point(232, 286), AutoSize = true };
        _eventCombo = new ComboButton { Location = new Point(232, 306), Width = 150, Height = 24, Enabled = false };
        _eventCombo.SetItems(HookEvents, 0);
        _eventCombo.SelectedIndexChanged += _ => CommitHookFields();

        var matcherLabel = new Label { Text = "Matcher (blank = any)", Location = new Point(394, 286), AutoSize = true };
        _matcherBox = CreateTextBox(new Rectangle(394, 306, 160, 24));
        _matcherBox.Leave += (_, _) => CommitHookFields();
        _matcherBox.KeyDown += (_, e) => CommitOnEnter(e, CommitHookFields);

        var commandLabel = new Label { Text = "Command", Location = new Point(232, 342), AutoSize = true };
        _commandBox = CreateTextBox(new Rectangle(232, 362, 322, 24));
        _commandBox.Leave += (_, _) => CommitHookFields();
        _commandBox.KeyDown += (_, e) => CommitOnEnter(e, CommitHookFields);

        var argsLabel = new Label { Text = "Args (one per line)", Location = new Point(232, 394), AutoSize = true };
        _argsBox = CreateTextBox(new Rectangle(232, 414, 322, 80));
        _argsBox.Multiline = true;
        _argsBox.Height = 80;
        _argsBox.Leave += (_, _) => CommitHookFields();

        var closeButton = new DarkButton { Text = "Close", Location = new Point(454, 520), Width = 100 };
        closeButton.Click += (_, _) => Close();

        foreach (var button in new[] { newFeatureButton, _deleteFeatureButton, _addHookButton, _removeHookButton, closeButton })
            StyleButton(button);

        Controls.AddRange(new Control[]
        {
            featuresLabel, _featureList, newFeatureButton, _deleteFeatureButton,
            separator, nameLabel, _nameBox, descriptionLabel, _descriptionBox,
            hooksLabel, _hookList, _addHookButton, _removeHookButton, hookSeparator,
            eventLabel, _eventCombo, matcherLabel, _matcherBox, commandLabel, _commandBox,
            argsLabel, _argsBox, closeButton,
        });

        RefreshFeatureList();
        if (initialFeatureId is { } id)
            SelectByIdIfPresent(id);
    }

    /// <summary>Lets an external caller (TrayApplicationContext) jump an already-open editor to a
    /// specific feature - the constructor's own initialFeatureId only helps for a brand new
    /// instance.</summary>
    public void SelectFeatureById(Guid id)
    {
        RefreshFeatureList();
        SelectByIdIfPresent(id);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var useDarkMode = 1;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _manager.FeaturesChanged -= OnFeaturesChanged;
        base.Dispose(disposing);
    }

    /// <summary>Keeps the feature list's own on/off text and the currently-selected feature/hook's
    /// fields in sync with a change made elsewhere (most commonly the Claude Pipeline widget's own
    /// switch, toggled while this editor happens to be open) - RefreshFeatureList already preserves
    /// the current selection by Id.</summary>
    private void OnFeaturesChanged(object? sender, EventArgs e) => RefreshFeatureList();

    private void RefreshFeatureList()
    {
        var previousId = _selectedFeature?.Id;

        _featureList.Items.Clear();
        foreach (var feature in _manager.Features)
            _featureList.Items.Add(feature);

        if (previousId is { } id)
            SelectByIdIfPresent(id);
        else if (_featureList.Items.Count > 0)
            _featureList.SelectedIndex = 0;
    }

    private void SelectByIdIfPresent(Guid id)
    {
        for (var i = 0; i < _featureList.Items.Count; i++)
        {
            if (((PipelineFeature)_featureList.Items[i]!).Id == id)
            {
                _featureList.SelectedIndex = i;
                return;
            }
        }

        if (_featureList.Items.Count > 0)
            _featureList.SelectedIndex = 0;
    }

    private void SelectFeature(int index)
    {
        _selectedFeature = index >= 0 && index < _featureList.Items.Count ? (PipelineFeature)_featureList.Items[index]! : null;
        _deleteFeatureButton.Enabled = _selectedFeature is not null;
        PopulateFeatureFields();
    }

    private void PopulateFeatureFields()
    {
        _isPopulating = true;
        try
        {
            var hasFeature = _selectedFeature is not null;
            _nameBox.Enabled = hasFeature;
            _descriptionBox.Enabled = hasFeature;
            _addHookButton.Enabled = hasFeature;
            _nameBox.Text = _selectedFeature?.Name ?? string.Empty;
            _descriptionBox.Text = _selectedFeature?.Description ?? string.Empty;
        }
        finally
        {
            _isPopulating = false;
        }

        RefreshHookList();
        if (_hookList.Items.Count > 0)
            _hookList.SelectedIndex = 0;
        else
            SelectHook(-1);
    }

    private void CommitFeatureFields()
    {
        if (_isPopulating || _selectedFeature is null)
            return;

        var name = _nameBox.Text.Trim();
        _selectedFeature.Name = name.Length > 0 ? name : _selectedFeature.Name;
        _selectedFeature.Description = _descriptionBox.Text;
        CommitFeature();
        _featureList.Invalidate();
    }

    private void AddFeature()
    {
        var feature = _manager.CreateFeature($"Feature {_manager.Features.Count + 1}");
        SelectFeatureById(feature.Id);
    }

    private void DeleteSelectedFeature()
    {
        if (_selectedFeature is null)
            return;

        var result = MessageBox.Show(this, $"Delete \"{_selectedFeature.Name}\"?", "Delete Feature",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
            return;

        _manager.DeleteFeature(_selectedFeature.Id);
        RefreshFeatureList();
    }

    private void RefreshHookList()
    {
        _isPopulating = true;
        try
        {
            _hookList.Items.Clear();
            if (_selectedFeature is not null)
                foreach (var hook in _selectedFeature.Hooks)
                    _hookList.Items.Add(hook);
        }
        finally
        {
            _isPopulating = false;
        }
    }

    private void SelectHook(int index)
    {
        _selectedHook = _selectedFeature is not null && index >= 0 && index < _hookList.Items.Count
            ? (PipelineHookSpec)_hookList.Items[index]!
            : null;
        _removeHookButton.Enabled = _selectedHook is not null;
        PopulateHookFields();
    }

    private void PopulateHookFields()
    {
        _isPopulating = true;
        try
        {
            var hasHook = _selectedHook is not null;
            _eventCombo.Enabled = hasHook;
            _matcherBox.Enabled = hasHook;
            _commandBox.Enabled = hasHook;
            _argsBox.Enabled = hasHook;

            var eventIndex = Math.Max(0, Array.IndexOf(HookEvents, _selectedHook?.Event ?? HookEvents[0]));
            _eventCombo.SetItems(HookEvents, eventIndex);
            _matcherBox.Text = _selectedHook?.Matcher ?? string.Empty;
            _commandBox.Text = _selectedHook?.Command ?? string.Empty;
            _argsBox.Text = _selectedHook is null ? string.Empty : string.Join(Environment.NewLine, _selectedHook.Args);
        }
        finally
        {
            _isPopulating = false;
        }
    }

    private void CommitHookFields()
    {
        if (_isPopulating || _selectedHook is null || _selectedFeature is null)
            return;

        _selectedHook.Event = HookEvents[Math.Max(0, _eventCombo.SelectedIndex)];
        var matcher = _matcherBox.Text.Trim();
        _selectedHook.Matcher = matcher.Length > 0 ? matcher : null;
        _selectedHook.Command = _commandBox.Text.Trim();
        _selectedHook.Args = _argsBox.Text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim()).Where(a => a.Length > 0).ToList();

        CommitFeature();
        _hookList.Invalidate();
    }

    private void AddHook()
    {
        if (_selectedFeature is null)
            return;

        _selectedFeature.Hooks.Add(new PipelineHookSpec());
        CommitFeature();
        RefreshHookList();
        _hookList.SelectedIndex = _hookList.Items.Count - 1;
    }

    private void RemoveSelectedHook()
    {
        if (_selectedFeature is null || _selectedHook is null)
            return;

        _selectedFeature.Hooks.Remove(_selectedHook);
        CommitFeature();
        RefreshHookList();
        if (_hookList.Items.Count > 0)
            _hookList.SelectedIndex = 0;
        else
            SelectHook(-1);
    }

    /// <summary>Persists the currently-selected feature - re-applies its live hook entries in
    /// ~/.claude/settings.json if it's currently enabled (see ClaudePipelineManager.UpdateFeature),
    /// so an edit made here to an already-toggled-on feature takes effect immediately.</summary>
    private void CommitFeature()
    {
        if (_selectedFeature is null)
            return;
        _manager.UpdateFeature(_selectedFeature);
    }

    private static void CommitOnEnter(KeyEventArgs e, Action commit)
    {
        if (e.KeyCode != Keys.Enter)
            return;
        commit();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private static ListBox CreateList(Rectangle bounds, DrawItemEventHandler drawItem)
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
        list.DrawItem += drawItem;
        list.HandleCreated += (_, _) => NativeMethods.SetWindowTheme(list.Handle, "", "");
        return list;
    }

    private static void DrawFeatureListItem(object? sender, DrawItemEventArgs e)
    {
        var list = (ListBox)sender!;
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (var background = new SolidBrush(selected ? AppTheme.Hover : AppTheme.Field))
            e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index < 0 || e.Index >= list.Items.Count)
            return;

        var feature = (PipelineFeature)list.Items[e.Index]!;
        var text = feature.Enabled ? $"{feature.Name}  (On)" : feature.Name;
        TextRenderer.DrawText(e.Graphics, text, list.Font, e.Bounds, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private static void DrawHookListItem(object? sender, DrawItemEventArgs e)
    {
        var list = (ListBox)sender!;
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (var background = new SolidBrush(selected ? AppTheme.Hover : AppTheme.Field))
            e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index < 0 || e.Index >= list.Items.Count)
            return;

        var hook = (PipelineHookSpec)list.Items[e.Index]!;
        var matcher = string.IsNullOrEmpty(hook.Matcher) ? "any" : hook.Matcher;
        var text = $"{hook.Event} [{matcher}] → {hook.Command}";
        TextRenderer.DrawText(e.Graphics, text, list.Font, e.Bounds, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
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
        box.HandleCreated += (_, _) => NativeMethods.SetWindowTheme(box.Handle, "", "");
        WireDisabledColor(box);
        return box;
    }

    private static void StyleButton(Button button)
    {
        AppTheme.StyleButton(button);
        button.BackColor = AppTheme.Field;
        WireDisabledColor(button);
    }

    private static void WireDisabledColor(Control control)
    {
        control.ForeColor = control.Enabled ? AppTheme.Text : AppTheme.DisabledText;
        control.EnabledChanged += (_, _) => control.ForeColor = control.Enabled ? AppTheme.Text : AppTheme.DisabledText;
    }
}
