using DesktopTool.Features.Layouts.Native;

namespace DesktopTool.Features.Layouts;

/// <summary>Owns every saved LayoutProfile and persists them, the same relationship FenceManager
/// has to FenceModel/FenceStore. No live Form per profile the way a fence gets one - a layout has
/// nothing to show until it's actually run (see RunLayoutAsync/WindowPlacer), so there's no
/// equivalent of FenceManager's _forms dictionary here.</summary>
public sealed class LayoutManager
{
    private readonly LayoutStore _store = new();
    private readonly List<LayoutProfile> _profiles = new();

    public IReadOnlyList<LayoutProfile> Profiles => _profiles;

    /// <summary>Raised whenever a profile is added, renamed/edited, deleted, or duplicated - lets the
    /// Layout Launcher widget's own row list repaint immediately (see LayoutLauncherWidget's
    /// subscription) instead of only catching up whenever some unrelated interaction (a row hover,
    /// say) happens to trigger a repaint of its own.</summary>
    public event EventHandler? ProfilesChanged;

    public void Load()
    {
        _profiles.Clear();
        _profiles.AddRange(_store.Load());
    }

    public LayoutProfile CreateLayout(string name)
    {
        var profile = new LayoutProfile { Name = name };
        _profiles.Add(profile);
        Save();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    /// <summary>"Save Current Layout" - a new profile pre-populated from whatever's actually open
    /// and where it's actually sitting right now (see WindowPlacer.CaptureCurrentLayout), instead of
    /// building one program-by-program through the editor.</summary>
    public LayoutProfile CaptureCurrentLayout(string name)
    {
        var profile = new LayoutProfile { Name = name, Entries = WindowPlacer.CaptureCurrentLayout() };
        _profiles.Add(profile);
        Save();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public void UpdateLayout(LayoutProfile profile)
    {
        var index = _profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
            _profiles[index] = profile;
        Save();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteLayout(Guid id)
    {
        _profiles.RemoveAll(p => p.Id == id);
        _launchErrors.Remove(id);
        Save();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Copy" in the launcher widget - a fully independent clone (fresh Id from
    /// LayoutProfile's own default, entries deep-copied so editing the copy's placements never
    /// mutates the source) named "{source.Name} (Copy)", inserted directly after the source rather
    /// than appended at the end so the duplicate shows up right next to what it was copied from.
    /// Null if id no longer matches anything - the profile could have been deleted from elsewhere
    /// (the editor, say) while the caller was still looking at a now-stale list.</summary>
    public LayoutProfile? DuplicateLayout(Guid id)
    {
        var index = _profiles.FindIndex(p => p.Id == id);
        if (index < 0)
            return null;

        var source = _profiles[index];
        var copy = new LayoutProfile
        {
            Name = $"{source.Name} (Copy)",
            Entries = source.Entries.Select(CloneEntry).ToList(),
        };
        _profiles.Insert(index + 1, copy);
        Save();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return copy;
    }

    private static LayoutEntry CloneEntry(LayoutEntry entry) => new()
    {
        ProgramPath = entry.ProgramPath,
        Arguments = entry.Arguments,
        WindowTitleHint = entry.WindowTitleHint,
        Url = entry.Url,
        Command = entry.Command,
        TerminalShellExe = entry.TerminalShellExe,
        TargetMonitor = entry.TargetMonitor,
        Placement = entry.Placement,
        Minimized = entry.Minimized,
        CustomX = entry.CustomX,
        CustomY = entry.CustomY,
        CustomWidth = entry.CustomWidth,
        CustomHeight = entry.CustomHeight,
    };

    /// <summary>Raised once RunLayoutAsync finishes with at least one entry that never actually
    /// launched (see WindowPlacer.RunAsync's own doc comment) - the profile's own name plus the
    /// program file name(s) that failed, for whoever wants to surface it (the tray's own balloon
    /// notification, currently the only subscriber - see TrayApplicationContext).</summary>
    public event EventHandler<(string ProfileName, IReadOnlyList<string> FailedPrograms)>? LaunchFailed;

    /// <summary>The program file names that failed to launch the last time this profile was run - see
    /// GetLaunchError. In-memory only, deliberately not persisted to disk: a stale error from a
    /// previous session isn't meaningful once the underlying cause (a moved file, say) might already
    /// be fixed, and there's no "last run" concept in the saved JSON to hang it off anyway.</summary>
    private readonly Dictionary<Guid, IReadOnlyList<string>> _launchErrors = new();

    /// <summary>The program file names that failed to launch the last time this profile was run, or
    /// null if it hasn't been run this session, or its last run had no failures - for a caller that
    /// wants to show an error indicator on the layout itself (Layout Launcher's own row - see
    /// LayoutLauncherWidget.PaintListRow/GetRowTooltipTarget) rather than just the one-shot
    /// LaunchFailed notification.</summary>
    public IReadOnlyList<string>? GetLaunchError(Guid id) => _launchErrors.TryGetValue(id, out var failures) ? failures : null;

    /// <summary>Fire-and-forget-safe from the caller's perspective (a tray click handler, say) -
    /// nothing here throws; a bad entry is reported through LaunchFailed/GetLaunchError above rather
    /// than an exception, and one bad program in a layout never blocks the rest from running.</summary>
    public async Task RunLayoutAsync(Guid id)
    {
        var profile = _profiles.Find(p => p.Id == id);
        if (profile is null)
            return;

        var failures = await WindowPlacer.RunAsync(profile.Entries);
        if (failures.Count > 0)
        {
            _launchErrors[id] = failures;
            LaunchFailed?.Invoke(this, (profile.Name, failures));
        }
        else
        {
            _launchErrors.Remove(id);
        }
    }

    private void Save() => _store.Save(_profiles);
}
