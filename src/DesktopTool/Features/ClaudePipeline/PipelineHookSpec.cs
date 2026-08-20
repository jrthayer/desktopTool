namespace DesktopTool.Features.ClaudePipeline;

/// <summary>One Claude Code hook entry a PipelineFeature contributes - matches the shape Claude Code
/// itself expects under settings.json's "hooks" object: hooks.{Event} is an array of matcher groups,
/// each holding one or more {type:"command", command, args} objects. Matcher is null for an event
/// (like UserPromptSubmit/Stop) that doesn't filter by tool name - see ClaudeSettingsSync for how
/// that's told apart from an actual (possibly empty-string) matcher.</summary>
public sealed class PipelineHookSpec
{
    public string Event { get; set; } = "PreToolUse";
    public string? Matcher { get; set; }
    public string Command { get; set; } = string.Empty;
    public List<string> Args { get; set; } = new();

    public PipelineHookSpec Clone() => new()
    {
        Event = Event,
        Matcher = Matcher,
        Command = Command,
        Args = new List<string>(Args),
    };

    /// <summary>Same event/matcher/command/args, ignoring List reference identity - used both by
    /// ClaudeSettingsSync (to find an existing hook object in settings.json) and by
    /// PipelineFeature.AppliedHooks (to tell whether an edited Hooks entry still matches what's
    /// actually applied).</summary>
    public bool DeepEquals(PipelineHookSpec other) =>
        Event == other.Event && Matcher == other.Matcher && Command == other.Command
        && Args.SequenceEqual(other.Args);
}
