using System.Text.Json;
using System.Text.RegularExpressions;

namespace CxAgent.Core.Hooks;

/// <summary>
/// Reads a <c>hooks</c> block: <c>{ "&lt;Event&gt;": [ { "matcher": "…", "hooks": [ { "type": …, … } ] } ] }</c>.
///
/// <para>A BAD ENTRY COSTS ONLY ITSELF. Hooks files are hand-written and shared through repositories; one
/// typo must not take every other hook down with it, or a session start with nothing guarding it. Each
/// entry that cannot be read becomes a problem naming where it is, and the rest load.</para>
/// </summary>
public static class HookConfigParser
{
    /// <summary>Keys of the block that are settings, not events.</summary>
    private static readonly HashSet<string> SettingKeys = new(StringComparer.Ordinal) { "defaultModel" };

    public static HookParse Parse(JsonElement block, HookSource source)
    {
        var hooks = new List<HookDefinition>();
        var problems = new List<string>();
        if (block.ValueKind != JsonValueKind.Object)
            return new([], ["hooks must be an object of event names."]);

        foreach (var ev in block.EnumerateObject())
        {
            if (SettingKeys.Contains(ev.Name)) continue;
            if (ev.Value.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"{ev.Name}: must be a list of matcher groups.");
                continue;
            }

            var groupIndex = 0;
            foreach (var group in ev.Value.EnumerateArray())
            {
                groupIndex++;
                if (group.ValueKind != JsonValueKind.Object
                    || !group.TryGetProperty("hooks", out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    problems.Add($"{ev.Name} group {groupIndex}: needs a \"hooks\" list.");
                    continue;
                }

                string? matcher = null;
                if (group.TryGetProperty("matcher", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    matcher = m.GetString();
                    // CHECKED HERE, so a bad pattern is reported once at load rather than silently matching
                    // nothing on every call — a guard that never fires looks exactly like one that passed.
                    if (!ValidPattern(matcher))
                    {
                        problems.Add($"{ev.Name} group {groupIndex}: matcher \"{matcher}\" is not a valid pattern.");
                        continue;
                    }
                }

                var hookIndex = 0;
                foreach (var h in list.EnumerateArray())
                {
                    hookIndex++;
                    if (Read(ev.Name, matcher, h, source) is { } hook) hooks.Add(hook);
                    else problems.Add($"{ev.Name} hook {hookIndex}: {Why(h)}");
                }
            }
        }
        return new(hooks, problems);
    }

    private static HookDefinition? Read(string ev, string? matcher, JsonElement h, HookSource source)
    {
        if (h.ValueKind != JsonValueKind.Object) return null;
        var type = Str(h, "type");
        if (type is null) return null;
        var command = Str(h, "command");
        if (type == "command" && string.IsNullOrWhiteSpace(command)) return null;

        return new HookDefinition(ev, string.IsNullOrEmpty(matcher) ? null : matcher, type, source)
        {
            Name = Str(h, "name"),
            Command = command,
            Shell = Str(h, "shell"),
            TimeoutSeconds = h.TryGetProperty("timeout", out var t) && t.TryGetInt32(out var secs) && secs > 0 ? secs : null,
            OnFailureBlock = Str(h, "onFailure") == "block",
            Model = Str(h, "model"),
            Url = Str(h, "url"),
            Raw = h.Clone(),
        };
    }

    private static string Why(JsonElement h) =>
        h.ValueKind != JsonValueKind.Object ? "must be an object."
        : Str(h, "type") is null ? "needs a \"type\"."
        : "a command hook needs a \"command\".";

    private static string? Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool ValidPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        try { _ = new Regex(pattern); return true; }
        catch (ArgumentException) { return false; }
    }
}
