namespace DesktopTool.Features.ClaudePipeline;

/// <summary>A user-defined Claude Code pipeline feature - a named, toggleable bundle of hook entries
/// (see PipelineHookSpec). Enabling one writes its Hooks into ~/.claude/settings.json (see
/// ClaudeSettingsSync.Apply); disabling removes exactly what was last written (AppliedHooks), not
/// whatever Hooks currently says - see AppliedHooks' own doc comment for why.</summary>
public sealed class PipelineFeature
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Feature";
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public List<PipelineHookSpec> Hooks { get; set; } = new();

    /// <summary>A snapshot of exactly which hook entries ClaudeSettingsSync.Apply last wrote into
    /// settings.json for this feature - null while not currently applied. Removal (ClaudeSettingsSync.
    /// Remove) always undoes this snapshot rather than re-deriving it from the current Hooks list, so
    /// editing a feature's command/args after it's already enabled can never leave an orphaned entry
    /// behind in settings.json: the edit changes Hooks, but AppliedHooks (and so what a subsequent
    /// Remove looks for) still reflects what's actually sitting in the file until the next Apply.</summary>
    public List<PipelineHookSpec>? AppliedHooks { get; set; }
}
