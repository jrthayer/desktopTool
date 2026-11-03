using DesktopTool.Features.FenceLocker.Criteria;
using DesktopTool.UI;

namespace DesktopTool.Features.FenceLocker.UI;

/// <summary>Sets up a lock: the reminder note and any mix of ways in (any one opens it). Used two
/// ways - editing a lock loadout (EditLoadout: a name, then Save/Delete, nothing placed yet) and
/// editing the lock already on one fence (EditFence: Save/Remove lock/Lock/History, after the
/// fence's own lock has been met, so a lock can't be taken off without meeting it). Both start with
/// no ways in until you add some.</summary>
internal sealed class LockEditorForm : Form
{
    private enum Outcome { None, Saved, Removed, LockNow }

    private readonly TextBox? _name;
    private readonly TextBox _reason = CriterionUi.MakeTextBox(360, multiline: true);
    private readonly FlowLayoutPanel _criteriaPanel = CriterionUi.MakeColumn();
    private readonly List<(Control Section, ICriterionEditor Editor)> _editors = new();
    private readonly Label _error;

    private Outcome _outcome;
    private string _savedName = string.Empty;
    private string _savedReason = string.Empty;
    private List<LockCriterion> _savedCriteria = new();

    /// <summary>Creates (loadout null) or edits a loadout, saving it to FenceLocks.Loadouts.</summary>
    public static void EditLoadout(LockLoadout? loadout, IWin32Window? owner)
    {
        var isNew = loadout is null;
        using var editor = new LockEditorForm(
            isNew ? "New Lock" : $"Edit Lock \"{loadout!.Name}\"",
            name: loadout?.Name ?? string.Empty,
            reason: loadout?.Reason ?? string.Empty,
            criteria: loadout?.Criteria ?? new List<LockCriterion>(),
            removeLabel: isNew ? null : "Delete",
            showLockNow: false,
            historyFenceId: null);
        editor.ShowDialog(owner);

        switch (editor._outcome)
        {
            case Outcome.Saved:
                FenceLocks.Loadouts.Save(new LockLoadout
                {
                    Id = loadout?.Id ?? Guid.NewGuid(),
                    Name = editor._savedName,
                    Reason = editor._savedReason,
                    Criteria = editor._savedCriteria,
                });
                break;
            case Outcome.Removed when loadout is not null:
                FenceLocks.Loadouts.Remove(loadout.Id);
                break;
        }
    }

    /// <summary>Edits the lock on one fence - after an unlock prompt if the fence is locked.</summary>
    public static void EditFence(Guid fenceId, string fenceName, IWin32Window? owner)
    {
        var service = FenceLocks.Service;
        if (!service.EnsureUnlocked(fenceId, fenceName, owner))
            return;

        var existing = service.GetLock(fenceId);
        using var editor = new LockEditorForm(
            existing is null ? $"Lock \"{fenceName}\"" : $"Change lock on \"{fenceName}\"",
            name: null,
            reason: existing?.Reason ?? string.Empty,
            criteria: existing?.Criteria ?? new List<LockCriterion>(),
            removeLabel: existing is null ? null : "Remove lock",
            showLockNow: service.GetStatus(fenceId) == FenceLockStatus.UnlockedThisSession,
            historyFenceId: fenceId);
        editor.ShowDialog(owner);

        switch (editor._outcome)
        {
            case Outcome.Saved:
                service.SetLock(new FenceLock { FenceId = fenceId, Reason = editor._savedReason, Criteria = editor._savedCriteria });
                break;
            case Outcome.Removed:
                service.RemoveLock(fenceId);
                break;
            case Outcome.LockNow:
                service.LockAgain(fenceId);
                break;
        }
    }

    private LockEditorForm(string title, string? name, string reason, List<LockCriterion> criteria,
        string? removeLabel, bool showLockNow, Guid? historyFenceId)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = AppTheme.Font;
        BackColor = AppTheme.Body;
        ForeColor = AppTheme.Text;

        var column = CriterionUi.MakeColumn();

        if (name is not null)
        {
            column.Controls.Add(CriterionUi.MakeLabel("Name"));
            _name = CriterionUi.MakeTextBox(360);
            _name.Text = name;
            column.Controls.Add(_name);
        }

        column.Controls.Add(CriterionUi.MakeLabel("Why are you locking it? (shown every time it asks to be unlocked)"));
        _reason.Text = reason;
        column.Controls.Add(_reason);

        column.Controls.Add(CriterionUi.MakeLabel("Ways to open it - any one is enough:"));
        column.Controls.Add(_criteriaPanel);
        foreach (var criterion in criteria)
            AddSection(criterion);

        var add = CriterionUi.MakeButton("Add a way in ▾", 130);
        add.Click += (_, _) => ShowAddMenu(add);
        column.Controls.Add(add);

        _error = CriterionUi.MakeLabel(string.Empty);
        _error.ForeColor = AppTheme.Warning;
        _error.Visible = false;
        column.Controls.Add(_error);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        var save = CriterionUi.MakeButton("Save");
        save.Click += (_, _) => Save();
        buttons.Controls.Add(save);
        AcceptButton = save;

        if (removeLabel is not null)
        {
            var remove = CriterionUi.MakeButton(removeLabel, 100);
            remove.Click += (_, _) => Finish(Outcome.Removed);
            buttons.Controls.Add(remove);
        }

        if (showLockNow)
        {
            var lockNow = CriterionUi.MakeButton("Lock");
            lockNow.Click += (_, _) => Finish(Outcome.LockNow);
            buttons.Controls.Add(lockNow);
        }

        if (historyFenceId is { } fenceId)
        {
            var history = CriterionUi.MakeButton("History...");
            history.Click += (_, _) => UnlockHistoryForm.Open(this, fenceId);
            buttons.Controls.Add(history);
        }

        var cancel = CriterionUi.MakeButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(cancel);
        CancelButton = cancel;
        column.Controls.Add(buttons);

        Controls.Add(column);
    }

    private void Finish(Outcome outcome)
    {
        _outcome = outcome;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowAddMenu(Control anchor)
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            BackColor = AppTheme.Body,
            ForeColor = AppTheme.Text,
            Font = AppTheme.Font,
            ShowImageMargin = false,
        };
        foreach (var kind in CriterionUi.Kinds)
        {
            var item = menu.Items.Add(kind.DisplayName);
            // A lock only needs one of each kind.
            item.Enabled = !_editors.Any(e => e.Section.Tag as CriterionKind == kind);
            item.Click += (_, _) => AddSection(kind.Create());
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void AddSection(LockCriterion criterion)
    {
        var kind = CriterionUi.KindOf(criterion);
        var editor = kind.CreateEditor(criterion);

        var section = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = AppTheme.FieldDark,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 4, 0, 4),
            Tag = kind,
        };

        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var title = CriterionUi.MakeLabel(kind.DisplayName);
        title.Font = new Font(AppTheme.Font, FontStyle.Bold);
        title.AutoSize = false;
        title.Size = new Size(270, 24);
        title.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(title);
        var remove = CriterionUi.MakeButton("Remove", 70);
        remove.Margin = Padding.Empty;
        remove.Click += (_, _) =>
        {
            _criteriaPanel.Controls.Remove(section);
            _editors.RemoveAll(e => e.Section == section);
            section.Dispose();
        };
        header.Controls.Add(remove);

        section.Controls.Add(header);
        section.Controls.Add(editor.View);
        _criteriaPanel.Controls.Add(section);
        _editors.Add((section, editor));
    }

    private void Save()
    {
        if (_name is not null && string.IsNullOrWhiteSpace(_name.Text))
        {
            ShowError("Give the lock a name.");
            return;
        }

        var criteria = new List<LockCriterion>();
        foreach (var (_, editor) in _editors)
        {
            if (editor.Build(out var error) is not { } criterion)
            {
                ShowError(error ?? "Something above isn't filled in yet.");
                return;
            }
            criteria.Add(criterion);
        }

        // A lock with no way in could never be opened again - not even to remove it.
        if (criteria.Count == 0)
        {
            ShowError("Add at least one way to open it, using \"Add a way in\".");
            return;
        }

        _savedName = _name?.Text.Trim() ?? string.Empty;
        _savedReason = _reason.Text.Trim();
        _savedCriteria = criteria;
        Finish(Outcome.Saved);
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visible = true;
    }
}
