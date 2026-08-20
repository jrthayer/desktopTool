namespace DesktopTool.Features.ClaudePipeline;

/// <summary>Owns every user-defined PipelineFeature and persists them, the same relationship
/// LayoutManager has to LayoutProfile/LayoutStore - plus SetEnabled, which is this manager's own
/// addition: flips a feature's Enabled state and reconciles ~/.claude/settings.json to match via
/// ClaudeSettingsSync.</summary>
public sealed class ClaudePipelineManager
{
    private readonly ClaudePipelineFeatureStore _store;
    private readonly ClaudeSettingsSync _sync = new();
    private readonly List<PipelineFeature> _features = new();

    public IReadOnlyList<PipelineFeature> Features => _features;

    /// <summary>Raised whenever a feature is added, edited, deleted, or toggled - lets the Claude
    /// Pipeline widget's own row list repaint immediately, same idea as LayoutManager.ProfilesChanged.</summary>
    public event EventHandler? FeaturesChanged;

    /// <summary>Raised when reading or writing ~/.claude/settings.json fails (missing permissions,
    /// the file being locked by another process, malformed JSON a hand edit left behind, etc.) - the
    /// widget surfaces this as a message box rather than silently losing the toggle. The feature's own
    /// Enabled/AppliedHooks are left exactly as they were before the failed attempt.</summary>
    public event EventHandler<string>? SyncFailed;

    public ClaudePipelineManager(ClaudePipelineFeatureStore store)
    {
        _store = store;
    }

    public void Load()
    {
        _features.Clear();
        _features.AddRange(_store.Load());
    }

    public PipelineFeature CreateFeature(string name)
    {
        var feature = new PipelineFeature { Name = name };
        _features.Add(feature);
        Save();
        FeaturesChanged?.Invoke(this, EventArgs.Empty);
        return feature;
    }

    /// <summary>Commits an edit made in the editor form - if the feature is currently enabled, re-
    /// applies it (removing whatever the old Hooks produced, applying the new ones) so an edit to a
    /// live feature takes effect immediately instead of only on the next manual toggle.</summary>
    public void UpdateFeature(PipelineFeature feature)
    {
        var index = _features.FindIndex(f => f.Id == feature.Id);
        if (index < 0)
            return;
        _features[index] = feature;

        if (feature.Enabled)
        {
            try
            {
                _sync.Remove(feature);
                _sync.Apply(feature);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                SyncFailed?.Invoke(this, ex.Message);
            }
        }

        Save();
        FeaturesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteFeature(Guid id)
    {
        var feature = _features.Find(f => f.Id == id);
        if (feature is null)
            return;

        if (feature.Enabled)
        {
            try
            {
                _sync.Remove(feature);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                SyncFailed?.Invoke(this, ex.Message);
                return; // Leave the feature (and its live hook entries) in place rather than silently orphaning them.
            }
        }

        _features.Remove(feature);
        Save();
        FeaturesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What a Claude Pipeline widget row click does - applies or removes this feature's own
    /// hook entries in ~/.claude/settings.json (see ClaudeSettingsSync), then persists the new Enabled
    /// state only once that actually succeeded, so a sync failure never leaves the on-disk feature
    /// list claiming a state settings.json doesn't actually reflect.</summary>
    public void SetEnabled(Guid id, bool enabled)
    {
        var feature = _features.Find(f => f.Id == id);
        if (feature is null || feature.Enabled == enabled)
            return;

        try
        {
            if (enabled)
                _sync.Apply(feature);
            else
                _sync.Remove(feature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SyncFailed?.Invoke(this, ex.Message);
            return;
        }

        feature.Enabled = enabled;
        Save();
        FeaturesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Save() => _store.Save(_features);
}
