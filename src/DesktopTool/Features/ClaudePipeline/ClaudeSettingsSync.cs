using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopTool.Features.ClaudePipeline;

/// <summary>The only piece that touches Claude Code's real global config (~/.claude/settings.json).
/// Reads/writes it as a loose JsonObject tree, not a strongly-typed class, so every field this app
/// doesn't know about (permissions, theme, autoUpdatesChannel, hooks belonging to something else
/// entirely - e.g. tokensave's own PreToolUse/UserPromptSubmit/Stop hooks) round-trips untouched.
/// Never identifies "its own" hook entries by anything other than exact deep-equality against a
/// snapshot it wrote itself (PipelineFeature.AppliedHooks) - a hook this class didn't add is never
/// touched, removed, or reordered.</summary>
public sealed class ClaudeSettingsSync
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Ensures every hook in feature.Hooks is present in settings.json (adding whichever
    /// aren't already there - matched by exact event/matcher/command/args equality, so calling this
    /// twice in a row for the same feature is a no-op the second time), then snapshots exactly what
    /// was written into AppliedHooks so a later Remove undoes precisely this, not whatever Hooks might
    /// say by then.</summary>
    public void Apply(PipelineFeature feature)
    {
        var root = LoadRoot();
        var hooks = GetOrCreateObject(root, "hooks");

        foreach (var spec in feature.Hooks)
        {
            var eventArray = GetOrCreateArray(hooks, spec.Event);
            var group = FindOrCreateGroup(eventArray, spec.Matcher);
            var hookArray = GetOrCreateArray(group, "hooks");
            if (!hookArray.OfType<JsonObject>().Any(h => HookEquals(h, spec)))
                hookArray.Add(BuildHookObject(spec));
        }

        SaveRoot(root);
        feature.AppliedHooks = feature.Hooks.Select(h => h.Clone()).ToList();
    }

    /// <summary>Undoes exactly what the last Apply wrote (feature.AppliedHooks), removing each hook
    /// object, then its matcher group if that group's own hooks array is now empty, then the whole
    /// hooks.{Event} array if that's now empty too - a no-op for anything this class didn't add
    /// (a differently-shaped entry a user or another tool added under the same event/matcher is left
    /// alone, since removal only ever matches by exact deep-equality).</summary>
    public void Remove(PipelineFeature feature)
    {
        var applied = feature.AppliedHooks;
        feature.AppliedHooks = null;
        if (applied is not { Count: > 0 })
            return;

        var root = LoadRoot();
        if (root["hooks"] is not JsonObject hooks)
            return;

        foreach (var spec in applied)
        {
            if (hooks[spec.Event] is not JsonArray eventArray)
                continue;

            for (var i = eventArray.Count - 1; i >= 0; i--)
            {
                if (eventArray[i] is not JsonObject group || !MatcherEquals(group, spec.Matcher))
                    continue;
                if (group["hooks"] is not JsonArray hookArray)
                    continue;

                for (var j = hookArray.Count - 1; j >= 0; j--)
                {
                    if (hookArray[j] is JsonObject hookObj && HookEquals(hookObj, spec))
                        hookArray.RemoveAt(j);
                }

                if (hookArray.Count == 0)
                    eventArray.RemoveAt(i);
            }

            if (eventArray.Count == 0)
                hooks.Remove(spec.Event);
        }

        if (hooks.Count == 0)
            root.Remove("hooks");

        SaveRoot(root);
    }

    private static JsonObject LoadRoot()
    {
        if (!File.Exists(SettingsPath))
            return new JsonObject();

        var json = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
    }

    private static void SaveRoot(JsonObject root)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(SettingsPath, root.ToJsonString(WriteOptions));
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing)
            return existing;
        var created = new JsonArray();
        parent[key] = created;
        return created;
    }

    /// <summary>Finds the matcher group (an object with a "matcher" string plus a "hooks" array)
    /// within an event's own array whose matcher equals the requested one, creating a fresh group at
    /// the end if none matches. A null matcher never matches a non-null one and vice versa - see
    /// GetMatcher's own comment.</summary>
    private static JsonObject FindOrCreateGroup(JsonArray eventArray, string? matcher)
    {
        foreach (var node in eventArray)
        {
            if (node is JsonObject group && GetMatcher(group) == matcher)
                return group;
        }

        var created = new JsonObject { ["hooks"] = new JsonArray() };
        if (matcher is not null)
            created["matcher"] = matcher;
        eventArray.Add(created);
        return created;
    }

    /// <summary>A group with no "matcher" property at all and one with matcher:null are both treated
    /// as "no matcher" (null here) - deliberately not distinguished, since Claude Code itself treats
    /// both the same way and a hand-edited settings.json could use either.</summary>
    private static bool MatcherEquals(JsonObject group, string? matcher) => GetMatcher(group) == matcher;

    private static string? GetMatcher(JsonObject group) => GetString(group["matcher"]);

    private static JsonObject BuildHookObject(PipelineHookSpec spec)
    {
        var obj = new JsonObject
        {
            ["type"] = "command",
            ["command"] = spec.Command,
        };
        if (spec.Args.Count > 0)
            obj["args"] = new JsonArray(spec.Args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        return obj;
    }

    private static bool HookEquals(JsonObject hookObj, PipelineHookSpec spec)
    {
        if (GetString(hookObj["command"]) != spec.Command)
            return false;

        var args = (hookObj["args"] as JsonArray)?.Select(n => GetString(n) ?? string.Empty).ToList() ?? new List<string>();
        return args.SequenceEqual(spec.Args);
    }

    /// <summary>Never throws on an unexpected shape (a non-string "matcher"/"command"/arg in a hand-
    /// edited settings.json, say) - just reports it as absent, so a malformed neighbor entry can never
    /// crash this class, only fail to match.</summary>
    private static string? GetString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
