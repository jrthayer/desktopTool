using DesktopTool.UI;

namespace DesktopTool.Features.FenceLocker.UI;

/// <summary>The prompt a locked fence shows instead of doing what was asked. It leads with the
/// reason you gave when you locked it, then one section per way in (a time window says when it
/// opens; a password or special event takes input). Unlock tries every section and lets you in on
/// the first that works.</summary>
internal sealed class UnlockDialog : Form
{
    private readonly List<UnlockInput> _inputs = new();
    private readonly Label _error;

    public UnlockGrant? Grant { get; private set; }

    /// <summary>Shows the prompt; returns what let you in, or null if you cancelled.</summary>
    public static UnlockGrant? Prompt(FenceLock fenceLock, string fenceName, IWin32Window? owner)
    {
        using var dialog = new UnlockDialog(fenceLock, fenceName, FenceLocks.Service.Now);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Grant : null;
    }

    private UnlockDialog(FenceLock fenceLock, string fenceName, DateTime now)
    {
        Text = $"\"{fenceName}\" is locked";
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

        var heading = CriterionUi.MakeLabel($"\"{fenceName}\" is locked.");
        heading.Font = new Font(AppTheme.Font, FontStyle.Bold);
        column.Controls.Add(heading);

        if (!string.IsNullOrWhiteSpace(fenceLock.Reason))
        {
            var reminder = new Label
            {
                Text = $"You locked this because:\n{fenceLock.Reason}",
                AutoSize = true,
                MaximumSize = new Size(340, 0),
                BackColor = AppTheme.FieldDark,
                ForeColor = AppTheme.Warning,
                Padding = new Padding(8),
                Margin = new Padding(0, 6, 0, 8),
            };
            column.Controls.Add(reminder);
        }

        foreach (var criterion in fenceLock.Criteria)
        {
            var input = CriterionUi.KindOf(criterion).CreateUnlockInput(criterion, now);
            _inputs.Add(input);
            input.View.Margin = new Padding(0, 6, 0, 2);
            column.Controls.Add(input.View);
        }

        _error = CriterionUi.MakeLabel(string.Empty);
        _error.ForeColor = AppTheme.Warning;
        _error.Visible = false;
        column.Controls.Add(_error);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        var canUnlock = _inputs.Any(i => i.TryUnlock is not null);
        if (canUnlock)
        {
            var unlock = CriterionUi.MakeButton("Unlock");
            unlock.Click += (_, _) => TryUnlock();
            buttons.Controls.Add(unlock);
            AcceptButton = unlock;
        }
        var cancel = CriterionUi.MakeButton(canUnlock ? "Cancel" : "Close");
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(cancel);
        CancelButton = cancel;
        column.Controls.Add(buttons);

        Controls.Add(column);
    }

    private void TryUnlock()
    {
        var errors = new List<string>();
        foreach (var input in _inputs)
        {
            if (input.TryUnlock is null)
                continue;
            var attempt = input.TryUnlock();
            if (attempt.Grant is { } grant)
            {
                Grant = grant;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            if (attempt.Attempted && attempt.Error is { } error)
                errors.Add(error);
        }

        _error.Text = errors.Count > 0 ? string.Join("\n", errors) : "Fill in one of the ways in above.";
        _error.Visible = true;
    }
}
