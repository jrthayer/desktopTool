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

    /// <summary>Same idea as CreateFence/CreateFenceLike (see that one's own doc comment) but for a
    /// folder fence - seeded from an existing folder fence's own settings instead of the defaults,
    /// used by FolderFenceForm's own "+" Copy Folder Fence button (see its ExtraButtons). Copies
    /// every Base style setting plus the two settings a folder fence has (Hide Labels/OCD Fence
    /// Sizing), the source's own size (not position - see NextDefaultBounds, same cascading
    /// placement CreateFenceLike itself uses), and - unlike CreateFenceLike's own deliberate "not a
    /// clone of its contents" stance for an ordinary fence's Files - RootFolderPath and Name too, so
    /// the copy mirrors the exact same real folder the source does rather than landing back in the
    /// empty "+" state needing to be pointed at one all over again. Never copies CurrentSubPath,
    /// though - the copy starts browsing at that folder's own root, not wherever the source
    /// currently happens to be browsed into. No cross-type Corner Radius re-clamp needed the way
    /// ConvertFromFence needs (source is already a folder fence, so it's already within
    /// FolderFenceForm's own lower ceiling).</summary>
    public void CreateFolderFenceLike(Guid sourceId)
    {
        var source = _models.Find(m => m.Id == sourceId);
        if (source is null)
            return;

        var model = new FolderFenceModel
        {
            Name = source.Name,
            RootFolderPath = source.RootFolderPath,
            Bounds = NextDefaultBounds(source.Bounds.Size),
            HideLabels = source.HideLabels,
            OcdFenceSizing = source.OcdFenceSizing,
            TintColor = source.TintColor,
            TintIsExact = source.TintIsExact,
            HeaderDarkness = source.HeaderDarkness,
            Opacity = source.Opacity,
            FullOpacityOnHover = source.FullOpacityOnHover,
            TintStrength = source.TintStrength,
            Margin = source.Margin,
            CornerRadius = source.CornerRadius,
            TitleFontSize = source.TitleFontSize,
            TitleAlignment = source.TitleAlignment,
            HeaderBorderMode = source.HeaderBorderMode,
            LightBorder = source.LightBorder,
            HideHeader = source.HideHeader,
            HeaderCloseButton = source.HeaderCloseButton,
        };
        _models.Add(model);
        ShowFolderFence(model);
        Save();
    }

    /// <summary>Converts an empty fence (source, already detached from FenceManager - see its own
    /// TakeForConversion) into a new folder fence pointed at folderPath, in the same spot and
    /// carrying over every Base style setting (tint/opacity/margin/corner radius/etc, HideHeader/
    /// LightBorder/HeaderCloseButton) plus the two settings a fence and a folder fence both have
    /// (Hide Shortcut Names/OCD Fence Sizing) - everything CreateFenceLike itself would carry
    /// between two ordinary fences, translated across widget types. Never carries source's own Name
    /// across - a folder fence names itself after the folder on first assignment, same as dropping
    /// a folder onto its own empty "+" state does (see FolderFenceForm.SetRootFolder), so this
    /// mirrors that instead of keeping whatever the empty fence happened to be called.
    ///
    /// HideHeader/LightBorder are carried over like everything else, unlike an earlier version of
    /// this method that dropped them - both are now inert for a folder fence at the source (see
    /// FolderFenceForm.HideHeader's own override and ShowLightBorderOption's doc comment on why
    /// LightBorder needs no equivalent override), so there's no longer anything here that needs to
    /// know to skip them.
    ///
    /// Corner Radius is re-clamped to FolderFenceForm's own lower ceiling (its tab/diagonal
    /// proportions can't take as large a radius as a plain fence can) rather than carried over
    /// as-is, which could otherwise land above what the Corner Radius stepper would ever let this
    /// widget reach on its own.</summary>
    public void ConvertFromFence(FenceModel source, string folderPath)
    {
        var model = new FolderFenceModel
        {
            Bounds = source.Bounds,
            RootFolderPath = folderPath,
            HideLabels = source.HideLabels,
            OcdFenceSizing = source.OcdFenceSizing,
            TintColor = source.TintColor,
            TintIsExact = source.TintIsExact,
            HeaderDarkness = source.HeaderDarkness,
            Opacity = source.Opacity,
            FullOpacityOnHover = source.FullOpacityOnHover,
            TintStrength = source.TintStrength,
            Margin = source.Margin,
            CornerRadius = Math.Min(source.CornerRadius, FolderFenceCornerRadiusMax),
            TitleFontSize = source.TitleFontSize,
            TitleAlignment = source.TitleAlignment,
            HeaderBorderMode = source.HeaderBorderMode,
            LightBorder = source.LightBorder,
            HideHeader = source.HideHeader,
            HeaderCloseButton = source.HeaderCloseButton,
        };

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        model.Name = string.IsNullOrEmpty(folderName) ? folderPath : folderName;

        _models.Add(model);
        ShowFolderFence(model);
        Save();
    }

    // Mirrors FolderFenceForm.CornerRadiusMax - duplicated rather than referenced since that's a
    // protected instance property on the form, not reachable from here (or worth making public just
    // for this one cross-check).
    private const int FolderFenceCornerRadiusMax = 20;

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

    /// <summary>Finds the folder fence window (other than excludeId) whose window rect contains
    /// screenPoint - mirrors FenceManager.FindFenceAt, used when a folder fence's own grid-item drag
    /// (see FolderFenceForm.OnMouseUp/ComputeDragHint) is released over a *different* folder fence
    /// instead of an ordinary one, so a dragged subfolder can connect an empty target the same way
    /// dropping it there directly (OLE, or the "+" button) already would.</summary>
    internal FolderFenceForm? FindFolderFenceAt(Point screenPoint, Guid excludeId)
    {
        foreach (var (id, form) in _forms)
        {
            if (id != excludeId && form.Bounds.Contains(screenPoint))
                return form;
        }
        return null;
    }

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

    /// <summary>Where a copy (CreateFolderFenceLike) lands - cascading near the top-left corner
    /// rather than CenteredBounds' own dead-center placement, same reasoning/formula as
    /// FenceManager's own NextDefaultBounds: a duplicate should land near where you started, not
    /// stack exactly on top of the original or jump to the middle of the screen.</summary>
    private Rectangle NextDefaultBounds(Size? size = null)
    {
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var offset = (_forms.Count % 8) * 24;
        var resolvedSize = size ?? DefaultFolderFenceSize;
        return new Rectangle(workArea.Left + 80 + offset, workArea.Top + 80 + offset, resolvedSize.Width, resolvedSize.Height);
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
