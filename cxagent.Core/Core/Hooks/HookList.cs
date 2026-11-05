using System.Text.Json.Nodes;
using System.Text;

namespace CxAgent.Core.Hooks;

/// <summary>The words for a hook's state and source, shared by <c>/hooks</c> and the F8 tabs so the two cannot disagree.</summary>
public static class HookList
{
    /// <summary>Where a hook came from, as a short tag.</summary>
    public static string SourceTag(HookSource source) => source switch
    {
        HookSource.User => "system",
        HookSource.Project => "project",
        HookSource.Local => "local",
        _ => "app",
    };

    /// <summary>
    /// Whether this hook fires now, and if not, why: an event or type cxagent does not run yet, or a
    /// repository hook in a folder that is not trusted.
    /// </summary>
    public static string State(HookDefinition hook, bool trusted) =>
        !hook.Runs ? "not supported here"
        : hook.Source is HookSource.Project or HookSource.Local && !trusted ? "not running: folder not trusted"
        : "active";

    /// <summary>The <c>/hooks</c> listing, as markdown.</summary>
    /// <param name="changed">Project hooks files that differ from what the session runs.</param>
    /// <param name="onDisk">Those files as they are now, so the listing can show what a review would apply.</param>
    public static string Describe(HookRunner runner, IReadOnlyList<string> changed, HookSnapshot? onDisk = null)
    {
        var snapshot = runner.Snapshot;
        var trusted = runner.ProjectTrusted;
        var sb = new StringBuilder();
        if (snapshot.Hooks.Count == 0 && runner.Handlers.Count == 0)
            sb.Append("No hooks. Add one in F8 › Hooks, or in .cxagent/hooks.json.\n");
        else
        {
            sb.Append("| event | matcher | hook | from | state |\n|---|---|---|---|---|\n");
            foreach (var h in snapshot.Hooks.OrderBy(h => HookEventOrder(h.Event)).ThenBy(h => h.Source))
                sb.Append($"| {h.Event} | {Cell(h.Matcher ?? "any")} | {Cell(h.DisplayName)} | {SourceTag(h.Source)} | {State(h, trusted)} |\n");
            if (runner.Handlers.Count > 0)
                sb.Append($"\n{runner.Handlers.Count} handler(s) registered by the app.\n");
        }

        foreach (var file in changed)
            sb.Append($"\n{Path.GetFileName(file)} changed since this session read it. `/hooks review` applies it.\n");
        // WHAT A REVIEW WOULD APPLY, said before it is applied: the review is reading this, not typing a word.
        if (changed.Count > 0 && onDisk is not null && Changes(snapshot, onDisk) is { Count: > 0 } diff)
        {
            sb.Append("\n");
            foreach (var line in diff) sb.Append($"- `{line}`\n");
        }
        foreach (var p in snapshot.Problems)
            sb.Append($"\n{Path.GetFileName(p.File)}: {p.Message}\n");
        return sb.ToString();
    }

    /// <summary>
    /// The repository hooks in <paramref name="next"/> that <paramref name="running"/> does not have (<c>+</c>),
    /// and the other way round (<c>−</c>) — one line each, with the hook as written.
    ///
    /// <para>EVERY FIELD COUNTS. A review is only a review if a change anywhere in a hook shows: a header added to an
    /// http hook, an <c>allowedEnvVars</c> entry, an MCP tool swapped for another. Only the name is left out, since
    /// renaming a hook changes nothing it does.</para>
    /// </summary>
    public static IReadOnlyList<string> Changes(HookSnapshot running, HookSnapshot next)
    {
        static bool Project(HookDefinition h) => h.Source is HookSource.Project or HookSource.Local;
        var before = running.Hooks.Where(Project).Select(Key).ToList();
        var after = next.Hooks.Where(Project).Select(Key).ToList();
        return [.. after.Except(before).Select(k => "+ " + k), .. before.Except(after).Select(k => "− " + k)];
    }

    /// <summary>A hook as one line: where, when, and everything it is configured with but its name.</summary>
    public static string Key(HookDefinition h) =>
        $"{SourceTag(h.Source)} {h.Event}{(h.Matcher is { } m ? $" [{m}]" : "")} {Canonical(h)}";

    private static string Canonical(HookDefinition h)
    {
        var o = h.Raw is { } raw && JsonNode.Parse(raw.GetRawText()) is JsonObject json ? json : new JsonObject
        {
            ["type"] = h.Type, ["command"] = h.Command, ["prompt"] = h.Prompt, ["model"] = h.Model, ["url"] = h.Url,
            ["server"] = h.Server, ["tool"] = h.Tool,
        };
        o.Remove("name");
        return Sorted(o).ToJsonString();
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Where(kv => kv.Value is not null).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Sorted(kv.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Sorted)]),
        _ => node?.DeepClone(),
    };

    private static int HookEventOrder(string ev)
    {
        var i = HookEvents.All.ToList().IndexOf(ev);
        return i < 0 ? int.MaxValue : i;
    }

    /// <summary>
    /// A cell's text with its pipes swapped for a broken bar. An escaped pipe is not honoured by every
    /// renderer this reaches, and a matcher like <c>write_file|replace_in_file</c> would split its row into
    /// extra columns; the broken bar reads the same and splits nothing.
    /// </summary>
    private static string Cell(string text) => text.Replace('|', '¦');
}
