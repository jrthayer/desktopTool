using DesktopTool.Features.Fences.UI;
using DesktopTool.Features.FolderFences.UI;
using DesktopTool.UI;

namespace DesktopTool.Features.FenceLocker.UI;

/// <summary>"Manage Locks" - every fence and folder fence that currently has a lock, with its status
/// ("Locked · opens today at 18:00", "Unlocked this session", ...). Clicking one shows what you can
/// do with it: Unlock, Lock, Change lock, Remove lock, its unlock history (see FenceLockMenu).
/// Updates live as locks change.</summary>
internal sealed class ManageLocksForm : Form
{
    private const int RowHeight = 30;

    private static ManageLocksForm? _open;

    private readonly ListBox _list;
    private readonly Label _empty;
    private readonly List<(Guid Id, string Name, bool IsFolder)> _rows = new();

    public static void Open(IWin32Window? owner)
    {
        if (_open is { IsDisposed: false })
        {
            _open.Activate();
            return;
        }
        var form = new ManageLocksForm();
        form.FormClosed += (_, _) => _open = null;
        _open = form;
        form.Show(owner);
    }

    private ManageLocksForm()
    {
        Text = "Manage Locks";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        MinimizeBox = false;
        ClientSize = new Size(440, 300);
        MinimumSize = new Size(320, 180);
        Font = AppTheme.Font;
        BackColor = AppTheme.Body;
        ForeColor = AppTheme.Text;

        var hint = new Label
        {
            Text = "Locked fences - click one to unlock it, change its lock, or remove it.",
            AutoSize = true,
            ForeColor = AppTheme.DisabledText,
            Location = new Point(12, 12),
        };

        // Just in case: opens locked fences without meeting their locks - each one asks why, and
        // it's logged as an "Executive override" (see FencePickerOverlay.Override).
        var overrideButton = CriterionUi.MakeButton("Executive Override...", 160);
        overrideButton.Location = new Point(12, ClientSize.Height - 12 - overrideButton.Height);
        overrideButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        overrideButton.Click += (_, _) => FencePickerOverlay.Override();

        _list = new ListBox
        {
            Location = new Point(12, 36),
            Size = new Size(ClientSize.Width - 24, ClientSize.Height - 48 - overrideButton.Height - 10),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = RowHeight,
            BorderStyle = BorderStyle.None,
            BackColor = AppTheme.Field,
            ForeColor = AppTheme.Text,
            IntegralHeight = false,
        };
        _list.DrawItem += DrawRow;
        _list.MouseClick += (_, e) =>
        {
            var index = _list.IndexFromPoint(e.Location);
            if (index >= 0 && index < _rows.Count)
                FenceLockMenu.Show(_rows[index].Id, _rows[index].Name, this, _list.PointToScreen(e.Location), includeHistory: true);
        };

        _empty = new Label
        {
            Text = "No fences are locked. Place a lock from the Fence Locker widget.",
            AutoSize = true,
            ForeColor = AppTheme.DisabledText,
            BackColor = AppTheme.Field,
            Location = new Point(24, 48),
        };

        Controls.Add(_empty);
        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(overrideButton);
        _empty.BringToFront();

        FenceLocks.Changed += OnLocksChanged;
        FormClosed += (_, _) => FenceLocks.Changed -= OnLocksChanged;
        Reload();
    }

    private void OnLocksChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
            BeginInvoke(Reload);
    }

    private void Reload()
    {
        var service = FenceLocks.Service;
        _rows.Clear();
        foreach (var widget in LayeredWidgetForm.LiveWidgets)
        {
            (Guid Id, string Name, bool IsFolder)? row = widget switch
            {
                FenceForm fence => (fence.FenceId, fence.FenceName, false),
                FolderFenceForm folder => (folder.FolderFenceId, folder.FolderFenceName, true),
                _ => null,
            };
            if (row is { } r && service.GetLock(r.Id) is not null)
                _rows.Add(r);
        }
        _rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in _rows)
            _list.Items.Add(row.Name);
        _list.EndUpdate();
        _empty.Visible = _rows.Count == 0;
    }

    private void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _rows.Count)
            return;
        var row = _rows[e.Index];
        var status = FenceLocks.GetStatus(row.Id);

        using (var background = new SolidBrush(e.Index % 2 == 0 ? AppTheme.Field : AppTheme.Body))
            e.Graphics.FillRectangle(background, e.Bounds);

        const int iconSize = 12;
        var iconRect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + (e.Bounds.Height - iconSize) / 2, iconSize, iconSize);
        FenceLockBadge.Paint(e.Graphics, iconRect, status);

        var statusText = FenceLockerWidget.StatusText(row.Id);
        var statusColor = status == FenceLockStatus.Locked ? AppTheme.Warning : AppTheme.DisabledText;
        var textLeft = iconRect.Right + 10;
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var statusWidth = Math.Min(e.Bounds.Width / 2, TextRenderer.MeasureText(statusText, Font).Width + 4);
        var statusRect = new Rectangle(e.Bounds.Right - 10 - statusWidth, e.Bounds.Y, statusWidth, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, row.IsFolder ? $"{row.Name} (folder)" : row.Name, Font,
            new Rectangle(textLeft, e.Bounds.Y, Math.Max(0, statusRect.X - 10 - textLeft), e.Bounds.Height), AppTheme.Text, flags);
        TextRenderer.DrawText(e.Graphics, statusText, Font, statusRect, statusColor, flags | TextFormatFlags.Right);
    }
}
