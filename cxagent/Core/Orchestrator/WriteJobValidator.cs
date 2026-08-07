using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Core.Orchestrator;

/// <summary>
/// The compile-time rules for a <c>file</c> write whose content comes from a dependency.
///
/// <para>ONE implementation, called by both PlanCompiler and ConsultJobCompiler. Every defect class
/// in this codebase — D7, D8, D11, D12, and the missing param validation found today — was a rule
/// added to one compiler and not its twin. A shared type is the only structural guard against that;
/// two copies of these rules would drift, and the failure is silent.</para>
/// </summary>
public static class WriteJobValidator
{
    /// <summary>
    /// True when this job's <c>content</c> will be supplied at run time by
    /// <c>JobExecutor.TryInjectDependencyContent</c> — a write/append with exactly one dependency
    /// and no content of its own.
    ///
    /// <para>Callers use this to SKIP the plugin's own validation for that job, because
    /// FileJobPlugin requires <c>content</c> for a write and would otherwise reject the plan before
    /// it could run. This is the one param that is legally absent at plan time and present at run
    /// time.</para>
    /// </summary>
    public static bool ContentComesFromDependency(string pluginType, string? action,
        bool hasContent, int dependencyCount) =>
        pluginType == "file"
        && action is "write" or "append"
        && !hasContent
        && dependencyCount == 1;

    /// <summary>
    /// Rejects a write whose content source is AMBIGUOUS: more than one dependency and nothing
    /// authored. Injection cannot choose between them, and picking one silently would write an
    /// arbitrary dependency's output to the user's file.
    ///
    /// <para>Rejected at COMPILE time, before an LLM call is spent on a plan that cannot work —
    /// the same point at which CrewAI rejects a bad context list. The message names the remedy
    /// rather than just the fault: a worker combines the outputs, and the write depends on it.
    /// That is already the documented shape in LlmAgentJobPlugin's own schema.</para>
    /// </summary>
    public static bool TryValidate(string localId, string pluginType, string? action,
        bool hasContent, int dependencyCount, out string? error)
    {
        error = null;

        if (pluginType != "file") return true;
        if (action is not ("write" or "append")) return true;
        if (hasContent) return true;            // explicit content always wins
        if (dependencyCount == 1) return true;  // injection supplies it at run time

        // ZERO dependencies and no content. FileJobPlugin's own "'content' is required" fires here,
        // but it teaches nothing: the model has just been told to omit `content` and take the output
        // of a dependency, and its mistake was forgetting the depends_on, not the content. Saying so
        // turns a confusing rejection into a one-line correction.
        if (dependencyCount == 0)
        {
            error = $"Job '{localId}' (file {action}) sets no 'content' and depends on nothing, so "
                  + "there is no output to write. Either add the producing job to `depends_on` — its "
                  + "output becomes the file's contents — or set 'content' explicitly.";
            return false;
        }

        error = $"Job '{localId}' (file {action}) depends on {dependencyCount} jobs and sets no "
              + "'content', so which output to write is ambiguous. Use ONE llm_agent job to combine "
              + "them and depend on that single job, or set 'content' explicitly.";
        return false;
    }

    /// <summary>
    /// Rejects a write whose <c>content</c> is nothing but a <c>{{…}}</c> reference.
    ///
    /// <para>The syntax is gone, so such a value is now LITERAL TEXT and would be written verbatim.
    /// Seen on the very first drive after removal: the model reached for its old habit, wrote
    /// <c>{{review_overflow.content}}</c> with no depends_on at all, and produced a 27-byte AUDIT.md
    /// holding that string — the exact defect the removal was meant to end, arriving through the
    /// front door instead of the back.</para>
    ///
    /// <para>Rejected rather than silently accepted because the intent is unmistakable: nobody
    /// writes a file whose entire contents are a brace expression. The message teaches the
    /// replacement, and the compile-time rejection routes it back through the repair loop.</para>
    /// </summary>
    public static bool TryValidateContent(string localId, string pluginType, string? action,
        string? content, out string? error)
    {
        error = null;

        if (pluginType != "file") return true;
        if (action is not ("write" or "append")) return true;
        if (content is null) return true;

        var trimmed = content.Trim();
        if (!trimmed.StartsWith("{{") || !trimmed.EndsWith("}}")) return true;

        error = $"Job '{localId}': 'content' is {trimmed}, which is literal text — there is no "
              + "reference syntax. To write another job's output, put that job in this job's "
              + "`depends_on` and omit `content` entirely; its output becomes the file's contents.";
        return false;
    }

    /// <summary>Reads the interesting bits off a params bag, tolerating JsonElement values (params
    /// become JsonElement after a SQLite round-trip, so a blind cast throws on every reload).</summary>
    public static (string? Action, bool HasContent, string? Content) Inspect(
        IReadOnlyDictionary<string, object?> values)
    {
        var action = values.TryGetValue("action", out var a) ? a?.ToString() : null;
        var content = values.TryGetValue("content", out var c) ? c?.ToString() : null;
        return (action, !string.IsNullOrEmpty(content), content);
    }
}
