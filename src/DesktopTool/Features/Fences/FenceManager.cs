using DesktopTool.Features.Fences.Native;
using DesktopTool.Features.Fences.UI;
using DesktopTool.Features.Layouts.Native;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.Fences;

public sealed class FenceManager : IDisposable
{
    private readonly FenceStore _store = new();
    private readonly DesktopListView _desktopListView = new();
    private readonly DesktopIconHider _iconHider;
    private readonly IDesktopAnchorStrategy _anchorStrategy;
    private readonly List<FenceModel> _models = new();
    private readonly Dictionary<Guid, FenceForm> _forms = new();
    private readonly RecycleBinChangeWatcher _recycleBinWatcher = new();

    // PruneDeadItems is wired to run on every fence activation (see FenceForm.OnActivated) as well
    // as startup and Show All, so it self-throttles to at most once per this interval - a stale
    // shortcut lingering a few extra seconds after an uninstall is fine; rescanning every click
    // (and re-prompting for the Recycle Bin confirmation) is not.
    private static readonly TimeSpan PruneCooldown = TimeSpan.FromSeconds(10);
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private bool _pruning;

    public SnapLineManager SnapLines { get; } = new();

    /// <summary>Forwards every live FenceForm's own FolderDroppedOnEmptyFence up a level - see that
    /// event's own doc comment for why FenceManager itself only relays this rather than acting on it
    /// (it has no notion of "Folder Fence" at all). Wired per-form in ShowFence below.</summary>
    public event EventHandler<(Guid FenceId, string FolderPath)>? FolderDroppedOnEmptyFence;

    public FenceManager()
    {
        // EmbeddedDesktopAnchorStrategy's SetParent mechanics work correctly (verified via
        // GetAncestor), but once truly placed behind the icon view, the fence becomes invisible
        // even in empty desktop areas, and mouse input there stops reaching it - the icon view
        // appears to paint an opaque background rather than leaving transparent gaps. See that
        // class's doc comment for details. Using the floating strategy so fences stay visible
        // and interactive.
        _anchorStrategy = new FloatingDesktopAnchorStrategy();
        _iconHider = new DesktopIconHider(_desktopListView);
        _recycleBinWatcher.Changed += RefreshRecycleBinIcons;
    }

    public void LoadAndShowAll()
    {
        _models.Clear();
        _models.AddRange(_store.Load());

        // Re-establishes hidden state for every already-fenced shortcut - the real files are only
        // ever restored on a clean exit (see Dispose), so on a normal launch this re-hides them
        // (and, for anyone upgrading from an older scheme, migrates them to the current one - see
        // DesktopIconHider.Hide); after a crash it's a harmless no-op since they're already hidden.
        // Hide can mutate Path/RealDesktopPath during that migration, so this needs its own Save.
        // Ignores Hide's own result here - a startup pass silently re-trying (and re-warning about)
        // something that's permanently un-hideable (e.g. a folder containing DesktopTool's own
        // running executable) on every single launch would be far more annoying than useful; see
        // AddFiles, where the same failure is worth surfacing once, at the moment it's added.
        //
        // Done before ShowFence below, not after - each FenceForm paints its own items (icon and
        // all) immediately in its constructor, reading item.Path as it stands at that moment. Doing
        // this migration/re-hide pass first means every fence's very first paint already sees the
        // settled, final path instead of wherever a pre-migration/not-yet-re-hidden item happened to
        // be sitting - hiding it out from under an already-painted fence used to occasionally leave
        // an item showing no icon at all until some later repaint (a hover, a resize) retried it
        // (see GetIcon's own comment on why a failed extraction isn't cached).
        foreach (var model in _models)
            foreach (var file in model.Files)
                if (!file.IsRecycleBin)
                    _iconHider.Hide(file);
        Save();

        // Drop anything that points at nothing (a program uninstalled since it was fenced, a file
        // deleted straight off the desktop) before the fences paint it - forced past the cooldown
        // since this is the one-shot startup pass.
        PruneDeadItems(force: true);

        foreach (var model in _models)
            ShowFence(model);
    }

    public void CreateFence()
    {
        var model = new FenceModel
        {
            Name = $"Fence {_models.Count + 1}",
            Bounds = CenteredBounds(),
        };
        _models.Add(model);
        ShowFence(model);
        Save();
    }

    /// <summary>Same idea as CreateFence, but seeded from an existing fence's settings (every
    /// IWidgetStyle knob, plus HideHeader/HeaderCloseButton/HideLabels/OCD sizing and size) instead
    /// of the defaults -
    /// see FenceForm's "+" button next to Settings. Deliberately doesn't copy Files or Bounds' own
    /// position: this is "another fence styled and sized the same way", not a clone of its contents
    /// or a stack-on-top-of-the-original duplicate.</summary>
    public void CreateFenceLike(Guid sourceId)
    {
        var source = _models.Find(m => m.Id == sourceId);
        if (source is null)
            return;

        var model = new FenceModel
        {
            Name = $"Fence {_models.Count + 1}",
            Bounds = NextDefaultBounds(source.Bounds.Size),
            HideLabels = source.HideLabels,
            HideHeader = source.HideHeader,
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
            HeaderCloseButton = source.HeaderCloseButton,
        };
        _models.Add(model);
        ShowFence(model);
        Save();
    }

    public void DeleteFence(Guid id)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null)
            return;
        var items = model.Files.ToList();

        // Removed up front so IsReferencedByAnyFence below doesn't just match this same fence: a
        // file only referenced here should restore, one still held by another fence shouldn't. If
        // moving any item back to the real desktop fails - and every fallback destination for that
        // also fails (see DesktopIconHider.Restore) - the model goes right back in rather than
        // deleting the fence anyway, so this never silently contradicts ConfirmDelete's "the files
        // inside it won't be deleted".
        _models.Remove(model);

        var stuck = items.Count(item => !IsReferencedByAnyFence(item.Path) && !_iconHider.Restore(item));
        if (stuck > 0)
        {
            _models.Add(model);
            MessageBox.Show(
                $"\"{model.Name}\" wasn't deleted: {stuck} file(s) in it couldn't be restored to " +
                "the desktop. Nothing was lost - check the hidden \"hiddenDesktop\" folder on your " +
                "desktop and your Explorer folder permissions, then try again.",
                "Fence Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_forms.Remove(id, out var form))
            // Deferred rather than disposed right here: this runs from deep inside the very form's
            // own WM_COMMAND handling (Delete Fence, clicked from its cog menu), so disposing it
            // immediately pulls the handle out from under code further up that same call stack
            // (TrackPopupMenuEx's owner-draw cleanup, OnMouseDown's post-processing) once it
            // unwinds - which throws ObjectDisposedException reading Handle. BeginInvoke defers the
            // actual Dispose to its own turn on the message loop, after all of that has unwound.
            form.BeginInvoke(new Action(form.Dispose));

        Save();
    }

    public void Dispose()
    {
        // Quitting Fence Tool should always leave an ordinary, fully-visible desktop behind, whether
        // or not anything is still fenced - LoadAndShowAll's own Hide pass re-derives and re-hides
        // whatever's still fenced on the next launch, so this deliberately doesn't Save afterward.
        foreach (var model in _models)
            foreach (var file in model.Files)
                if (!file.IsRecycleBin)
                    _iconHider.Restore(file);
        _recycleBinWatcher.Dispose();
        _desktopListView.Dispose();
        SnapLines.Dispose();
    }

    /// <summary>
    /// Adds dropped files to a fence's own contents - these are just paths the fence remembers and
    /// draws its own icon+label for (via FenceForm's paint logic), the same way NoFences and similar
    /// tools work. If a file lives directly on the real desktop, it's moved into a hidden folder so
    /// it doesn't sit doubled-up behind the fence's own drawing of it - see DesktopIconHider;
    /// anything dragged in from elsewhere is never touched on disk. Paths that don't exist or are
    /// already in this fence are silently skipped.
    /// </summary>
    public void AddFiles(Guid fenceId, IReadOnlyList<string> filePaths)
    {
        var model = _models.Find(m => m.Id == fenceId);
        if (model is null)
            return;

        var added = false;
        var stillVisible = new List<string>();
        foreach (var path in filePaths)
        {
            if (model.Files.Any(f => f.Path == path) || (!File.Exists(path) && !Directory.Exists(path)))
                continue;
            var item = new FenceItem { Path = path };
            model.Files.Add(item);
            if (!_iconHider.Hide(item))
                stillVisible.Add(Path.GetFileName(path));
            added = true;
        }

        if (stillVisible.Count > 0)
            MessageBox.Show(
                $"Added, but couldn't hide the real desktop icon for: {string.Join(", ", stillVisible)}. " +
                "It'll still show up doubled - once behind the fence's own drawing of it, and once on " +
                "the desktop - likely because it's in use or locked right now.",
                "Fence Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        if (added)
            Save();
    }

    /// <summary>The Recycle Bin's own well-known shell namespace CLSID string - not a real
    /// filesystem path, but SHGetFileInfo (see ShellIcons.ExtractLargeIcon) resolves it directly to
    /// the current, empty/full-aware system icon, so this doubles as FenceItem.Path for the
    /// synthetic trash item without needing any icon-code changes.</summary>
    public const string RecycleBinPath = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>Only one Recycle Bin item is allowed across every fence at once - there's only one
    /// real Recycle Bin, so more than one visual icon for it would be confusing rather than useful.
    /// Scans _models fresh rather than caching a flag, so this can never drift from the model data
    /// that's actually the source of truth.</summary>
    public bool HasRecycleBin => _models.Any(m => m.Files.Any(f => f.IsRecycleBin));

    /// <summary>Whether at least one fence is currently shown - read fresh from every live form's
    /// own Visible rather than cached, the same "scan live state, never track a shadow flag"
    /// reasoning as HasRecycleBin above. Lets Show/Hide All (tray, and Widget Manager's own Fences
    /// switch) always flip off the actual current state instead of two independent callers each
    /// keeping their own possibly-stale toggle bit.</summary>
    public bool AnyVisible => _forms.Values.Any(f => f.Visible);

    public bool IsRecycleBinAt(Guid fenceId, int index)
    {
        var model = _models.Find(m => m.Id == fenceId);
        return model is not null && index >= 0 && index < model.Files.Count && model.Files[index].IsRecycleBin;
    }

    /// <summary>Creates a new dedicated fence (mirroring CreateFence) holding just the single
    /// synthetic Recycle Bin item, and hides the real desktop icon (see RecycleBinIconManager) so it
    /// doesn't sit doubled-up. Triggered from Widget Manager's own Fence Trash Can switch rather than
    /// any particular fence's own settings, so there's no existing fence to target - a fresh one is
    /// the least ambiguous place to put it; the item can still be dragged into a different fence
    /// afterward like any other. No-ops if one already exists anywhere - see HasRecycleBin.
    ///
    /// Starts with no header (HideHeader), no label under the icon (HideLabels), and OCD Fence Sizing
    /// on, then fires ApplyOcdSizingIfEnabled once right after showing it - otherwise OCD Fence
    /// Sizing wouldn't actually tidy the bounds until the next manual resize (see FenceForm.OnDragEnd),
    /// leaving this brand-new fence sized like an ordinary one instead of wrapped tight around the
    /// single icon. MoveToBottomRight runs after that, not before, so it can anchor against this
    /// fence's own real wrapped-tight size instead of NextDefaultBounds' own placeholder one.</summary>
    public void AddRecycleBin()
    {
        if (HasRecycleBin)
            return;

        var model = new FenceModel
        {
            Name = "Recycle Bin",
            Bounds = NextDefaultBounds(),
            HideHeader = true,
            HideLabels = true,
            OcdFenceSizing = true,
        };
        model.Files.Add(new FenceItem { Path = RecycleBinPath, DisplayName = "Recycle Bin", IsRecycleBin = true });
        _models.Add(model);
        var form = ShowFence(model);
        form.ApplyOcdSizingIfEnabled();
        form.MoveToBottomRight(margin: 20);
        RecycleBinIconManager.SetHidden(true);
        Save();
    }

    /// <summary>Reverses AddRecycleBin - restores the real desktop icon's own visibility (see
    /// RemoveFile's own IsRecycleBin branch) and, since the fence this widget created for it holds
    /// nothing else in the ordinary case, deletes that now-empty fence outright rather than leaving a
    /// titled, contentless husk behind. If the user has since dragged other items into it, DeleteFence
    /// still isn't called - that fence has become a real one worth keeping, so only the trash item
    /// itself goes. No-ops if no Recycle Bin item exists anywhere - see HasRecycleBin.</summary>
    public void RemoveRecycleBin()
    {
        var model = _models.Find(m => m.Files.Any(f => f.IsRecycleBin));
        var item = model?.Files.Find(f => f.IsRecycleBin);
        if (model is null || item is null)
            return;

        RemoveFile(model.Id, item.Path);
        if (model.Files.Count == 0)
            DeleteFence(model.Id);
    }

    /// <summary>Sends files that were dropped directly onto a fence's trash cell straight to the
    /// Recycle Bin - these were never fence items to begin with (dragged fresh from Explorer), so
    /// there's no model to reconcile, unlike DeleteFencedItem below.</summary>
    public void DeletePaths(IReadOnlyList<string> paths, IntPtr ownerHwnd)
    {
        RecycleBinOperations.SendToRecycleBin(ownerHwnd, paths);
        // The shell change notification would land this in a moment anyway, but do it now so the
        // trash icon fills the instant the drop completes rather than a beat later.
        RefreshRecycleBinIcons();
    }

    /// <summary>Deletes an item that was already sitting in a fence by dragging it onto the trash
    /// cell (same fence or a different one), and only removes it from the fence model if the delete
    /// actually succeeded, so a locked file or a declined confirmation dialog leaves the item right
    /// where it was rather than vanishing regardless of outcome.</summary>
    public bool DeleteFencedItem(Guid fenceId, string path, IntPtr ownerHwnd)
    {
        var model = _models.Find(m => m.Id == fenceId);
        var item = model?.Files.Find(f => f.Path == path);
        if (model is null || item is null)
            return false;

        // Windows always restores a deleted file to wherever it was deleted FROM - if this item was
        // relocated into the hidden folder (see DesktopIconHider), deleting it from there means a
        // later Recycle Bin restore would put it back in that same hidden, un-fenced folder: not on
        // the real desktop, not in any fence, invisible either way. Moving it back to the real
        // desktop first (best-effort; RemoveFile below accepts the same rare failure case) means a
        // restore instead lands somewhere the user can actually see and re-fence it from. Updates
        // item.Path/RealDesktopPath in place on success; leaves them untouched on failure, so the
        // delete below still targets whatever the item's real current location is either way.
        if (item.RealDesktopPath is not null)
            _iconHider.Restore(item);

        if (!RecycleBinOperations.SendToRecycleBin(ownerHwnd, new[] { item.Path }))
            return false;

        model.Files.Remove(item);
        Save();
        RefreshRecycleBinIcons();
        return true;
    }

    public void RemoveFile(Guid fenceId, string path)
    {
        var model = _models.Find(m => m.Id == fenceId);
        var item = model?.Files.Find(f => f.Path == path);
        if (model is null || item is null || !model.Files.Remove(item))
            return;

        // The trash item has no real desktop icon of its own to restore - dragging it off a fence
        // just gives the real desktop's own Recycle Bin icon its visibility back instead.
        if (item.IsRecycleBin)
        {
            RecycleBinIconManager.SetHidden(false);
            Save();
            return;
        }

        // Only bring the real desktop icon back once no other fence holds this same path anymore -
        // and if that restore fails, put it right back rather than let this fence's removal
        // silently discard tracking of it.
        if (!IsReferencedByAnyFence(path) && !_iconHider.Restore(item))
        {
            model.Files.Add(item);
            var name = !string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : Path.GetFileNameWithoutExtension(item.Path);
            MessageBox.Show(
                $"Couldn't restore \"{name}\" to the desktop, so it's staying in this fence " +
                "instead. Check the hidden \"hiddenDesktop\" folder on your desktop and your " +
                "Explorer folder permissions if you'd rather place it yourself.",
                "Fence Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Save();
    }

    /// <summary>Reorders an item within its fence's own grid - dragging within the same fence,
    /// not a real desktop icon operation. newIndex is clamped to the valid range.</summary>
    public void MoveFile(Guid fenceId, string path, int newIndex)
    {
        var model = _models.Find(m => m.Id == fenceId);
        var item = model?.Files.Find(f => f.Path == path);
        if (model is null || item is null)
            return;

        model.Files.Remove(item);
        model.Files.Insert(Math.Clamp(newIndex, 0, model.Files.Count), item);
        Save();
    }

    /// <summary>Finds the fence window (other than excludeId) whose window rect contains
    /// screenPoint - used when an item drag started in one fence is released over another one, to
    /// tell whether it landed on a fence at all and which.</summary>
    internal FenceForm? FindFenceAt(Point screenPoint, Guid excludeId)
    {
        foreach (var (id, form) in _forms)
        {
            if (id != excludeId && form.Bounds.Contains(screenPoint))
                return form;
        }
        return null;
    }

    /// <summary>Restacks every OTHER tracked fence to the very bottom of the z-order, called right
    /// after activeId's own fence pushes itself there too (see FenceForm.OnDragEnd) - SetWindowPos's
    /// HWND_BOTTOM only ever repositions the one window it's called on, to underneath literally
    /// everything currently in the z-order, so restacking the others afterward (each individually,
    /// order among them doesn't matter) pushes every one of them below wherever activeId's own fence
    /// already settled, without this ever touching - or being able to elevate a fence above - real
    /// windows' own positions. Without this, a fence you just finished dragging would drop to the
    /// bottom of EVERYTHING, including every other fence it overlaps, rather than just behind real
    /// windows the way a fence is meant to.</summary>
    internal void RestackOtherFencesBehind(Guid activeId)
    {
        foreach (var (id, form) in _forms)
        {
            if (id != activeId)
                NativeMethods.SetWindowPos(form.Handle, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>Moves an item from one fence to another - unlike MoveFile (reorder within a single
    /// fence's own grid), this removes the item from its source fence's model and inserts it into
    /// the target fence's model, preserving its DisplayName. Silently dropped if the item can't be
    /// found in the source, or the target fence already holds this path (mirrors AddFiles' own
    /// silent-skip-on-duplicate behavior).</summary>
    public void MoveFileToFence(Guid sourceFenceId, Guid targetFenceId, string path, int targetIndex)
    {
        if (sourceFenceId == targetFenceId)
            return;

        var sourceModel = _models.Find(m => m.Id == sourceFenceId);
        var targetModel = _models.Find(m => m.Id == targetFenceId);
        var item = sourceModel?.Files.Find(f => f.Path == path);
        if (sourceModel is null || targetModel is null || item is null)
            return;

        sourceModel.Files.Remove(item);
        if (!targetModel.Files.Any(f => f.Path == path))
            targetModel.Files.Insert(Math.Clamp(targetIndex, 0, targetModel.Files.Count), item);

        Save();

        if (_forms.TryGetValue(targetFenceId, out var targetForm))
            targetForm.RefreshAfterExternalChange();
    }

    /// <summary>Sets an item's display name within this fence only - never renames the real file.</summary>
    public void RenameFile(Guid fenceId, string path, string displayName)
    {
        var model = _models.Find(m => m.Id == fenceId);
        var item = model?.Files.Find(f => f.Path == path);
        if (item is null)
            return;
        item.DisplayName = displayName;
        Save();
    }

    public void SetAllVisible(bool visible)
    {
        foreach (var form in _forms.Values)
            form.SetVisible(visible);
        if (visible)
            PruneDeadItems();
    }

    /// <summary>Re-extracts the trash cell's icon on whichever fence holds the Recycle Bin item, so
    /// it tracks the bin filling and emptying (RecycleBinChangeWatcher fires this for changes made
    /// anywhere; DeletePaths/DeleteFencedItem also call it directly for an instant flip). At most
    /// one fence ever holds the item, so this is cheap.</summary>
    private void RefreshRecycleBinIcons()
    {
        foreach (var model in _models)
            if (model.Files.Any(f => f.IsRecycleBin) && _forms.TryGetValue(model.Id, out var form))
                form.RefreshRecycleBinIcon();
    }

    /// <summary>Removes fenced items that now point at nothing. Two cases: a shortcut whose target
    /// program was uninstalled after it was fenced (the uninstaller can't find the .lnk to clean up
    /// because DesktopIconHider relocated it into the hidden folder, so it lingers forever pointing
    /// at a deleted exe); a Steam (.url) or Riot Client (.lnk) game shortcut whose game was
    /// uninstalled even though the shared launcher stayed (see GameLauncherProbe); or an item whose
    /// own file the user deleted directly. A still-present shortcut is sent to the Recycle Bin so
    /// it stays recoverable; an item whose file is already gone is just dropped from the fence.
    ///
    /// Deliberately conservative: a plain path is only "gone" when it's a fully-qualified local
    /// path on a drive that's present and ready (see LooksDefinitelyGone), and the game-launcher
    /// checks only fire on a definite "not installed" - so a disconnected network share, an
    /// unplugged USB drive, or an unrecognised shortcut can never trigger a false removal. Wired to
    /// run on every fence activation (FenceForm.OnActivated) plus startup and Show All, so it
    /// self-throttles to PruneCooldown; force bypasses that for the one-shot startup pass.</summary>
    public void PruneDeadItems(bool force = false)
    {
        if (_pruning)
            return;
        if (!force && DateTime.UtcNow - _lastPruneUtc < PruneCooldown)
            return;

        _pruning = true;
        try
        {
            var dead = new List<(FenceModel Model, FenceItem Item)>();
            var orphanedShortcuts = new List<string>();

            foreach (var model in _models)
            {
                foreach (var item in model.Files)
                {
                    if (item.IsRecycleBin)
                        continue;

                    // The fenced file itself is gone - nothing to send anywhere, just stop drawing it.
                    if (LooksDefinitelyGone(item.Path))
                    {
                        dead.Add((model, item));
                        continue;
                    }

                    if (!File.Exists(item.Path))
                        continue;

                    // The shortcut is still here, but the program (or game) it launches isn't - a
                    // Steam/Riot game whose install is gone, or an ordinary .lnk to a deleted exe.
                    var ext = Path.GetExtension(item.Path);
                    var isShortcut = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                                  || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
                    if (!isShortcut)
                        continue;

                    var launchesNothing = GameLauncherProbe.LaunchesUninstalledGame(item.Path)
                        || (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                            && ShortcutResolver.ResolveTarget(item.Path) is { } target
                            && LooksDefinitelyGone(target));
                    if (!launchesNothing)
                        continue;

                    dead.Add((model, item));
                    orphanedShortcuts.Add(item.Path);
                    if (item.RealDesktopPath is not null && File.Exists(item.RealDesktopPath))
                        orphanedShortcuts.Add(item.RealDesktopPath);
                }
            }

            if (dead.Count == 0)
                return;

            // Recycle the orphaned shortcut files first, as one batched undoable operation - only
            // forget the items if that actually goes through, so a declined confirmation or a locked
            // file leaves every fence exactly as it was (matches DeleteFencedItem's all-or-nothing).
            if (orphanedShortcuts.Count > 0)
            {
                var owner = _forms.Values.FirstOrDefault()?.Handle ?? IntPtr.Zero;
                if (!RecycleBinOperations.SendToRecycleBin(owner, orphanedShortcuts))
                    return;
            }

            var touched = new HashSet<Guid>();
            foreach (var (model, item) in dead)
                if (model.Files.Remove(item))
                    touched.Add(model.Id);

            if (touched.Count == 0)
                return;

            Save();
            foreach (var id in touched)
                if (_forms.TryGetValue(id, out var form))
                    form.RefreshAfterExternalChange();
        }
        finally
        {
            _lastPruneUtc = DateTime.UtcNow;
            _pruning = false;
        }
    }

    /// <summary>True only when path is a concrete local path we can be confident is genuinely gone:
    /// fully qualified, not a UNC share, on a drive that's present and ready. Anything unresolvable
    /// or remote returns false so the caller leaves the item alone rather than risk removing one
    /// whose target is just temporarily unreachable.</summary>
    private static bool LooksDefinitelyGone(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (File.Exists(path) || Directory.Exists(path))
            return false;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return false;

        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static readonly Size DefaultFenceSize = new(240, 200);

    private Rectangle NextDefaultBounds(Size? size = null)
    {
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var offset = (_forms.Count % 8) * 24;
        var resolvedSize = size ?? DefaultFenceSize;
        return new Rectangle(workArea.Left + 80 + offset, workArea.Top + 80 + offset, resolvedSize.Width, resolvedSize.Height);
    }

    /// <summary>Where a brand-new fence (CreateFence - "Add Fence"/what used to be the tray's own
    /// "New Fence") starts out - centered on the primary monitor's working area, rather than
    /// NextDefaultBounds' own cascading top-left corner. CreateFenceLike/AddRecycleBin still use
    /// NextDefaultBounds - a duplicate cascading near where you started, and the one-off Recycle Bin
    /// singleton, don't call for landing dead center the same way a plain new fence does.</summary>
    private static Rectangle CenteredBounds()
    {
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return new Rectangle(
            workArea.Left + (workArea.Width - DefaultFenceSize.Width) / 2,
            workArea.Top + (workArea.Height - DefaultFenceSize.Height) / 2,
            DefaultFenceSize.Width, DefaultFenceSize.Height);
    }

    private FenceForm ShowFence(FenceModel model)
    {
        var form = new FenceForm(model, this, _anchorStrategy);
        form.FolderDroppedOnEmptyFence += (_, folderPath) => FolderDroppedOnEmptyFence?.Invoke(this, (model.Id, folderPath));
        _forms[model.Id] = form;
        form.Show();
        form.Reanchor();
        return form;
    }

    /// <summary>Detaches fenceId's own model/form for conversion into a different kind of widget
    /// (currently only a Folder Fence - see FolderFenceManager.ConvertFromFence) and returns the
    /// model so the caller can carry its settings over. Null (and a no-op) if fenceId no longer
    /// matches anything alive, or it's picked up so much as a single item since the drop that
    /// triggered this - same race DeleteFence itself doesn't need to guard against, since nothing
    /// else deletes a fence out from under a still-in-flight drop.
    ///
    /// Skips DeleteFence's own restore-to-desktop handling entirely - safe only because this is
    /// exclusively reached with an empty fence (see the Files.Count guard below), which never has
    /// any real file to restore in the first place.</summary>
    internal FenceModel? TakeForConversion(Guid fenceId)
    {
        var model = _models.Find(m => m.Id == fenceId);
        if (model is null || model.Files.Count > 0)
            return null;
        _models.Remove(model);

        if (_forms.Remove(fenceId, out var form))
            // Deferred rather than disposed right here - same reasoning as DeleteFence's own
            // BeginInvoke: this runs from inside the very form's own OnDragDrop, further up this
            // same call stack.
            form.BeginInvoke(new Action(form.Dispose));

        Save();
        return model;
    }

    private bool IsReferencedByAnyFence(string path) => _models.Any(m => m.Files.Any(f => f.Path == path));

    /// <summary>Flushes every fence's current model state to disk - internal rather than private now
    /// that FenceForm calls this directly after mutating its own model in place (position, name,
    /// every IWidgetStyle property, Hide Header/Labels, OCD Sizing - see LayeredWidgetForm's own
    /// PersistStyle and FenceForm's HideHeader/Title setters), instead of routing each individual
    /// field change through its own dedicated FenceManager method. FenceManager's own remaining
    /// methods are everything that genuinely needs the shared _models/_forms collections - item-grid
    /// content, lifecycle, z-order, spatial/cross-fence queries - not per-field persistence for a
    /// single fence's own settings.</summary>
    internal void Save() => _store.Save(_models);
}
