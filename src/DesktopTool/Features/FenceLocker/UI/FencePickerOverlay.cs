using System.Drawing.Text;
using DesktopTool.Features.Fences.UI;
using DesktopTool.Features.FolderFences.UI;
using DesktopTool.UI;

namespace DesktopTool.Features.FenceLocker.UI;

/// <summary>
/// Click-on-fences picker shared by "place a lock loadout" (Place) and "executive override"
/// (Override): a full-screen click-catcher (the same technique as CopySettingsOverlay) that
/// highlights whichever eligible fence or folder fence is under the cursor and runs an action on
/// each one you click. Stays open so several fences can be done in a row; a click on anything
/// else, a right-click, or Escape finishes. A banner at the top of the main screen says what's
/// happening and how many fences it's done so far.
/// </summary>
internal sealed class FencePickerOverlay : Form
{
    private static readonly Color KeyColor = Color.FromArgb(255, 1, 2, 3);

    private readonly Func<Guid, bool> _isEligible;
    private readonly Func<Guid, string, IWin32Window, bool> _onPick;
    private readonly Func<int, string> _bannerText;
    private LayeredWidgetForm? _hovered;
    private int _done;

    /// <summary>Puts a copy of loadout on every fence clicked (asking for a fence's current lock
    /// first if it has one).</summary>
    public static void Place(LockLoadout loadout) => Start(
        _ => true,
        (id, name, owner) => FenceLocks.Service.ApplyLoadout(loadout, id, name, owner),
        done => $"Click fences to lock them with \"{loadout.Name}\"" + Count(done, "locked"));

    /// <summary>Executive override: each locked fence clicked asks why, then unlocks it for this
    /// session without its own lock being met - logged as an "Executive override" with the reason.</summary>
    public static void Override() => Start(
        id => FenceLocks.GetStatus(id) == FenceLockStatus.Locked,
        (id, name, owner) =>
        {
            if (OverrideReasonDialog.Ask(id, name, owner) is not { } reason)
                return false;
            FenceLocks.Service.Grant(id, name, new UnlockGrant("Executive override", reason));
            return true;
        },
        done => "Executive override - click a locked fence to open it" + Count(done, "overridden"));

    private static string Count(int done, string verb) =>
        done == 0 ? "   ·   Esc or right-click to finish" : $"   ·   {done} {verb}   ·   Esc or right-click to finish";

    private static void Start(Func<Guid, bool> isEligible, Func<Guid, string, IWin32Window, bool> onPick, Func<int, string> bannerText)
    {
        var overlay = new FencePickerOverlay(isEligible, onPick, bannerText);
        overlay.FormClosed += (_, _) => overlay.Dispose();
        overlay.Show();
        overlay.Activate();
    }

    private FencePickerOverlay(Func<Guid, bool> isEligible, Func<Guid, string, IWin32Window, bool> onPick, Func<int, string> bannerText)
    {
        _isEligible = isEligible;
        _onPick = onPick;
        _bannerText = bannerText;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        Cursor = Cursors.Hand;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        KeyPreview = true;
        DoubleBuffered = true;
        Font = new Font(AppTheme.Font.FontFamily, 10f);
    }

    private static (Guid Id, string Name)? FenceOf(LayeredWidgetForm? widget) => widget switch
    {
        FenceForm fence => (fence.FenceId, fence.FenceName),
        FolderFenceForm folder => (folder.FolderFenceId, folder.FolderFenceName),
        _ => null,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var target = LayeredWidgetForm.FindAt(PointToScreen(e.Location));
        if (FenceOf(target) is not { } fence || !_isEligible(fence.Id))
            target = null;
        if (ReferenceEquals(target, _hovered))
            return;

        _hovered?.SetPickTargetActive(false);
        _hovered = target;
        _hovered?.SetPickTargetActive(true);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            Finish();
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;

        if (FenceOf(_hovered) is not { } fence)
        {
            Finish();
            return;
        }

        // Any prompt the action shows has to sit above this overlay.
        TopMost = false;
        if (_onPick(fence.Id, fence.Name, this))
            _done++;
        TopMost = true;
        Activate();

        // A fence that just stopped being eligible (overridden) shouldn't stay highlighted.
        if (!_isEligible(fence.Id))
        {
            _hovered?.SetPickTargetActive(false);
            _hovered = null;
        }
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Finish();
    }

    private void Finish()
    {
        _hovered?.SetPickTargetActive(false);
        _hovered = null;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var text = _bannerText(_done);

        var size = TextRenderer.MeasureText(text, Font);
        var screen = Screen.PrimaryScreen!.Bounds;
        var pill = new Rectangle(
            PointToClient(new Point(screen.X + (screen.Width - size.Width - 28) / 2, screen.Y + 24)),
            new Size(size.Width + 28, size.Height + 14));

        e.Graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var path = RoundedRectPath.Full(pill, 10))
        using (var fill = new SolidBrush(AppTheme.FieldDark))
        using (var border = new Pen(AppTheme.Warning))
        {
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        }
        TextRenderer.DrawText(e.Graphics, text, Font, pill, AppTheme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW - keep it out of the taskbar/alt-tab
            return cp;
        }
    }
}

/// <summary>"Why are you overriding this lock?" - the one question an executive override asks.
/// Returns the reason, or null if cancelled; it won't accept an empty answer.</summary>
internal sealed class OverrideReasonDialog : Form
{
    private readonly TextBox _reason = CriterionUi.MakeTextBox(340, multiline: true);
    private readonly Label _error;

    public static string? Ask(Guid fenceId, string fenceName, IWin32Window? owner)
    {
        using var dialog = new OverrideReasonDialog(fenceId, fenceName);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._reason.Text.Trim() : null;
    }

    private OverrideReasonDialog(Guid fenceId, string fenceName)
    {
        Text = "Executive override";
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
        var heading = CriterionUi.MakeLabel($"Override the lock on \"{fenceName}\"?");
        heading.Font = new Font(AppTheme.Font, FontStyle.Bold);
        column.Controls.Add(heading);

        if (FenceLocks.Service.GetLock(fenceId) is { Reason.Length: > 0 } fenceLock)
        {
            column.Controls.Add(new Label
            {
                Text = $"You locked this because:\n{fenceLock.Reason}",
                AutoSize = true,
                MaximumSize = new Size(340, 0),
                BackColor = AppTheme.FieldDark,
                ForeColor = AppTheme.Warning,
                Padding = new Padding(8),
                Margin = new Padding(0, 6, 0, 8),
            });
        }

        column.Controls.Add(CriterionUi.MakeLabel("Why are you overriding it? (saved in the unlock history)"));
        column.Controls.Add(_reason);

        _error = CriterionUi.MakeLabel("Say why before overriding.");
        _error.ForeColor = AppTheme.Warning;
        _error.Visible = false;
        column.Controls.Add(_error);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        var ok = CriterionUi.MakeButton("Override");
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_reason.Text))
            {
                _error.Visible = true;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(ok);
        var cancel = CriterionUi.MakeButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(cancel);
        CancelButton = cancel;
        column.Controls.Add(buttons);

        Controls.Add(column);
    }
}
