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
        !HookEvents.Fires(hook.Event) || !HookDefinition.TypeRuns(hook.Type) ? "not supported here"
        : hook.Source is HookSource.Project or HookSource.Local && !trusted ? "not running: folder not trusted"
        : "active";

    /// <summary>The <c>/hooks</c> listing, as markdown.</summary>
    /// <param name="changed">Project hooks files that differ from what the session runs.</param>
    public static string Describe(HookRunner runner, IReadOnlyList<string> changed)
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
        foreach (var p in snapshot.Problems)
            sb.Append($"\n{Path.GetFileName(p.File)}: {p.Message}\n");
        return sb.ToString();
    }

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
