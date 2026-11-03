using DesktopTool.UI;

namespace DesktopTool.Features.FenceLocker.UI;

/// <summary>Every fence unlock, newest first: date, time, fence, how it was unlocked, and the reason
/// for a special event. Can be narrowed to one fence. Reads FenceLocks.Service.Log, and updates live
/// if an unlock happens while it's open.</summary>
internal sealed class UnlockHistoryForm : Form
{
    private const int RowHeight = 24;
    private static readonly int[] ColumnWidths = { 92, 52, 150, 100 };

    private static UnlockHistoryForm? _open;

    private readonly ComboButton _filter = new() { Width = 220, Height = 24 };
    private readonly ListBox _list;
    private readonly Label _empty;
    private readonly List<Guid?> _filterIds = new();
    private List<UnlockLogEntry> _shown = new();

    /// <summary>Opens the history (or brings the open one forward), filtered to fenceId if given.</summary>
    public static void Open(IWin32Window? owner, Guid? fenceId = null)
    {
        if (_open is { IsDisposed: false })
        {
            _open.SelectFence(fenceId);
            _open.Activate();
            return;
        }
        var form = new UnlockHistoryForm(fenceId);
        form.FormClosed += (_, _) => _open = null;
        _open = form;
        form.Show(owner);
    }

    private UnlockHistoryForm(Guid? fenceId)
    {
        Text = "Unlock History";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        MinimizeBox = false;
        ClientSize = new Size(720, 420);
        MinimumSize = new Size(480, 240);
        Font = AppTheme.Font;
        BackColor = AppTheme.Body;
        ForeColor = AppTheme.Text;

        var filterLabel = new Label { Text = "Show", AutoSize = true, Location = new Point(12, 16) };
        _filter.Location = new Point(56, 12);
        _filter.SelectedIndexChanged += _ => Reload();

        var header = new Panel { Location = new Point(12, 46), Height = RowHeight, BackColor = AppTheme.FieldDark, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        header.Width = ClientSize.Width - 24;
        header.Paint += (_, e) => PaintColumns(e.Graphics, header.ClientRectangle, new[] { "Date", "Time", "Fence", "Type", "Reason" }, AppTheme.DisabledText);

        _list = new ListBox
        {
            Location = new Point(12, 46 + RowHeight),
            Size = new Size(ClientSize.Width - 24, ClientSize.Height - 58 - RowHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = RowHeight,
            BorderStyle = BorderStyle.None,
            BackColor = AppTheme.Field,
            ForeColor = AppTheme.Text,
            IntegralHeight = false,
        };
        _list.DrawItem += DrawEntry;

        _empty = new Label
        {
            Text = "No unlocks yet.",
            AutoSize = true,
            ForeColor = AppTheme.DisabledText,
            BackColor = AppTheme.Field,
            Location = new Point(24, 58 + RowHeight),
        };

        Controls.Add(_empty);
        Controls.Add(_list);
        Controls.Add(header);
        Controls.Add(filterLabel);
        Controls.Add(_filter);
        _empty.BringToFront();

        PopulateFilter(fenceId);
        FenceLocks.Service.Log.Changed += OnLogChanged;
        FormClosed += (_, _) => FenceLocks.Service.Log.Changed -= OnLogChanged;
        Reload();
    }

    private void OnLogChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
            BeginInvoke(Reload);
    }

    /// <summary>"All fences", then every fence that has history, by the most recent name it was logged
    /// under (a fence can be renamed, or deleted since).</summary>
    private void PopulateFilter(Guid? select)
    {
        var fences = FenceLocks.Service.Log.Entries
            .GroupBy(e => e.FenceId)
            .Select(g => (Id: g.Key, Name: g.Last().FenceName))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (select is { } id && fences.All(f => f.Id != id))
            fences.Insert(0, (id, "This fence"));

        _filterIds.Clear();
        _filterIds.Add(null);
        _filterIds.AddRange(fences.Select(f => (Guid?)f.Id));
        var names = new List<string> { "All fences" };
        names.AddRange(fences.Select(f => f.Name));
        _filter.SetItems(names, Math.Max(0, _filterIds.IndexOf(select)));
    }

    private void SelectFence(Guid? fenceId)
    {
        PopulateFilter(fenceId);
        Reload();
    }

    private void Reload()
    {
        var selected = _filter.SelectedIndex >= 0 && _filter.SelectedIndex < _filterIds.Count ? _filterIds[_filter.SelectedIndex] : null;
        _shown = FenceLocks.Service.Log.Entries
            .Where(e => selected is null || e.FenceId == selected)
            .Reverse()
            .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in _shown)
            _list.Items.Add(entry);
        _list.EndUpdate();
        _empty.Visible = _shown.Count == 0;
    }

    private void DrawEntry(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _shown.Count)
            return;
        var entry = _shown[e.Index];
        var local = entry.Utc.ToLocalTime();

        using (var background = new SolidBrush(e.Index % 2 == 0 ? AppTheme.Field : AppTheme.Body))
            e.Graphics.FillRectangle(background, e.Bounds);

        PaintColumns(e.Graphics, e.Bounds,
            new[] { local.ToString("yyyy-MM-dd"), local.ToString("HH:mm"), entry.FenceName, entry.Type, entry.Reason ?? string.Empty },
            AppTheme.Text);
    }

    private static void PaintColumns(Graphics g, Rectangle bounds, IReadOnlyList<string> values, Color color)
    {
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var x = bounds.X + 8;
        for (var i = 0; i < values.Count; i++)
        {
            var width = i < ColumnWidths.Length ? ColumnWidths[i] : Math.Max(0, bounds.Right - x - 8);
            TextRenderer.DrawText(g, values[i], AppTheme.Font, new Rectangle(x, bounds.Y, width - 6, bounds.Height), color, flags);
            x += width;
        }
    }
}
