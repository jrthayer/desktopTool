using DesktopTool.UI;

namespace DesktopTool.Features.FenceLocker.UI;

/// <summary>What you can do with one fence's lock - shown from a row in Manage Locks and from the
/// fence's own padlock. Unlock... (while locked) only unlocks it for this session; Lock (while it's
/// unlocked this session) locks it again; Change lock... and Remove lock... each ask for the lock
/// first while it's locked. Manage Locks adds the fence's unlock history too.</summary>
internal static class FenceLockMenu
{
    public static void Show(Guid fenceId, string fenceName, Control owner, Point screenPoint, bool includeHistory)
    {
        var service = FenceLocks.Service;
        var status = service.GetStatus(fenceId);
        if (status == FenceLockStatus.None)
            return;

        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            BackColor = AppTheme.Body,
            ForeColor = AppTheme.Text,
            Font = AppTheme.Font,
            ShowImageMargin = false,
        };

        if (status == FenceLockStatus.Locked)
            menu.Items.Add("Unlock...", null, (_, _) => owner.BeginInvoke(() => service.EnsureUnlocked(fenceId, fenceName, owner)));
        if (status == FenceLockStatus.UnlockedThisSession)
            menu.Items.Add("Lock", null, (_, _) => service.LockAgain(fenceId));
        menu.Items.Add("Change lock...", null, (_, _) => owner.BeginInvoke(() => LockEditorForm.EditFence(fenceId, fenceName, owner)));
        menu.Items.Add("Remove lock...", null, (_, _) => owner.BeginInvoke(() => RemoveLock(fenceId, fenceName, owner)));
        if (includeHistory)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Unlock history...", null, (_, _) => UnlockHistoryForm.Open(owner, fenceId));
        }

        menu.Closed += (_, _) => owner.BeginInvoke(menu.Dispose);
        menu.Show(screenPoint);
    }

    private static void RemoveLock(Guid fenceId, string fenceName, IWin32Window owner)
    {
        var service = FenceLocks.Service;
        if (!service.EnsureUnlocked(fenceId, fenceName, owner))
            return;
        var answer = MessageBox.Show(owner, $"Remove the lock from \"{fenceName}\"?", "Remove Lock",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            service.RemoveLock(fenceId);
    }
}
