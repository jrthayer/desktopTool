using DesktopTool.Features.Fences;
using DesktopTool.Features.FolderFences.UI;

namespace DesktopTool.Features.FolderFences;

/// <summary>Parallel to FenceManager, but far smaller - a folder fence never owns file content of
/// its own (see FolderFenceModel.RootFolderPath's own doc comment), so there's no
/// DesktopIconHider, no per-item persistence, and no cross-fence drag/move to coordinate here.
/// Just enough lifecycle (create/delete/show/hide/save) to match every other manager on this
/// base.</summary>
public sealed class FolderFenceManager : IDisposable
{
    private readonly FolderFenceStore _store = new();
    private readonly FenceManager _fences;
    private readonly List<FolderFenceModel> _models = new();
    private readonly Dictionary<Guid, FolderFenceForm> _forms = new();

    /// <summary>fences is only ever handed to a FolderFenceForm's own base constructor, for the
    /// shared snap-against-other-widgets/custom-snap-lines behavior every LayeredWidgetForm gets
    /// for free - same reasoning as LayoutLauncherWidget/WidgetManagerWidget taking the same
    /// reference for the same purpose, not because a folder fence needs anything else from it.</summary>
    public FolderFenceManager(FenceManager fences)
    {
        _fences = fences;
    }

    public void LoadAndShowAll()
    {
        _models.Clear();
        _models.AddRange(_store.Load());
        foreach (var model in _models)
            ShowFolderFence(model);
    }

    public void CreateFolderFence()
    {
        var model = new FolderFenceModel
        {
            Name = $"Folder Fence {_models.Count + 1}",
            Bounds = CenteredBounds(),
        };
        _models.Add(model);
        ShowFolderFence(model);
        Save();
    }

    public void DeleteFolderFence(Guid id)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null)
            return;
        _models.Remove(model);

        if (_forms.Remove(id, out var form))
            // Deferred rather than disposed right here - same reasoning as FenceManager.DeleteFence:
            // this runs from deep inside the very form's own click handling further up the call
            // stack, so disposing immediately would pull the handle out from under it before that
            // unwinds.
            form.BeginInvoke(new Action(form.Dispose));

        Save();
    }

    /// <summary>Whether at least one folder fence is currently shown - read fresh from every live
    /// form's own Visible, same "scan live state, never track a shadow flag" reasoning as
    /// FenceManager.AnyVisible.</summary>
    public bool AnyVisible => _forms.Values.Any(f => f.Visible);

    public void SetAllVisible(bool visible)
    {
        foreach (var form in _forms.Values)
            form.SetVisible(visible);
    }

    /// <summary>Real disposal for app shutdown (TrayApplicationContext.OnExit) - no restore-to-
    /// desktop step needed first, unlike FenceManager.Dispose, since a folder fence's items were
    /// never moved or hidden in the first place (see FolderFenceModel.RootFolderPath's own doc
    /// comment).</summary>
    public void Dispose()
    {
        foreach (var form in _forms.Values)
            form.Dispose();
    }

    private static readonly Size DefaultFolderFenceSize = new(240, 200);

    private static Rectangle CenteredBounds()
    {
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return new Rectangle(
            workArea.Left + (workArea.Width - DefaultFolderFenceSize.Width) / 2,
            workArea.Top + (workArea.Height - DefaultFolderFenceSize.Height) / 2,
            DefaultFolderFenceSize.Width, DefaultFolderFenceSize.Height);
    }

    private FolderFenceForm ShowFolderFence(FolderFenceModel model)
    {
        var form = new FolderFenceForm(model, this, _fences);
        _forms[model.Id] = form;
        form.Show();
        return form;
    }

    internal void Save() => _store.Save(_models);
}
