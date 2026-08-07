using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;

namespace CxAgent.Core.Plugins;

/// <summary>
/// Bridges <see cref="WorkerTool"/> to the LLM tool-calling protocol: turns the allowed subset into
/// <see cref="ToolDefinition"/>s the model can call, and dispatches a returned <see cref="ToolCall"/>
/// back to the plugin that implements it.
///
/// <para>Every tool schema is generated from the plugin's own <see cref="JobSchema"/> — never
/// hand-written. <c>read_file</c>/<c>write_file</c> both dispatch to <see cref="Builtin.FileJobPlugin"/>
/// with <c>action</c> PINNED by the tool name, so the schema shown to the model omits `action`
/// entirely and a worker calling <c>read_file</c> cannot pass <c>action: "delete"</c>.</para>
/// </summary>
public static class WorkerToolset
{
    /// <summary>Cap on a tool-result string. Unbounded output (e.g. a large file read) would be fed
    /// into every subsequent ChatAsync call for the rest of the tool loop.</summary>
    public const int MaxToolResultChars = 8192;

    /// <param name="Params">
    /// The params THIS tool takes, in the order the model should read them. Selected from the
    /// plugin's real schema, never invented — a name absent from the plugin throws at build time.
    ///
    /// <para>Selecting rather than dumping is the whole point. The schema used to be the plugin's
    /// entire param list minus the pinned action, so <c>read_file</c> advertised nine parameters,
    /// five of them meaningless for reading (content, dest, replacement, regex, glob). A tool with
    /// nine optional-looking params and one required has no shape the model can read reliably; a
    /// live drive produced NINE consecutive <c>read_file {}</c> calls with empty arguments before
    /// its first good one.</para>
    /// </param>
    /// <param name="Required">
    /// The params without which the call cannot work. NOT taken from the plugin: FileJobPlugin
    /// serves six actions from one schema, so it can only mark <c>action</c> and <c>path</c>
    /// required — <c>content</c> cannot be required there because `read` does not use it. The
    /// consequence was a schema that told the model "path is all you need" for write_file and
    /// replace_in_file, both of which the plugin then rejects. Requiredness is a property of the
    /// TOOL, so it is stated per tool.
    /// </param>
    private sealed record ToolSpec(string Name, string PluginType, string? PinnedAction,
        string[] Params, string[] Required);

    // Ordered so WorkerTool.For's output is stable regardless of the caller's list order —
    // a stable tool order keeps the prompt (and provider-side caching of it) stable across calls.
    private static readonly IReadOnlyList<(WorkerTool Tool, ToolSpec Spec)> Specs = new[]
    {
        (WorkerTool.ReadFile, new ToolSpec("read_file", "file", "read",
            Params: ["path", "offset", "limit"], Required: ["path"])),
        // list/search are READ-ONLY and go to every role, including the judges. Through run_shell
        // they are `find` and `grep`, which raise a permission prompt for an operation that reads
        // nothing the role could not already read -- live drives stalled repeatedly on exactly
        // those approvals.
        (WorkerTool.ListFiles, new ToolSpec("list_files", "file", "list",
            Params: ["path", "pattern", "limit"], Required: ["path"])),
        (WorkerTool.SearchFiles, new ToolSpec("search_files", "file", "search",
            Params: ["path", "pattern", "regex", "glob", "limit"], Required: ["path", "pattern"])),
        (WorkerTool.WriteFile, new ToolSpec("write_file", "file", "write",
            Params: ["path", "content"], Required: ["path", "content"])),
        // Producers only: replace EDITS an existing file. write_file is whole-file, so changing one
        // function meant reproducing every other line from memory.
        (WorkerTool.ReplaceInFile, new ToolSpec("replace_in_file", "file", "replace",
            Params: ["path", "pattern", "replacement"],
            Required: ["path", "pattern", "replacement"])),
        // Unpinned: the shell plugin serves one action, so its whole schema IS this tool's.
        (WorkerTool.RunShell, new ToolSpec("run_shell", "shell", null,
            Params: ["command", "working_dir", "timeout_seconds"], Required: ["command"])),
        (WorkerTool.HttpRequest, new ToolSpec("http_request", "http", null,
            Params: ["url", "method", "headers", "body"], Required: ["url"])),
    };

    /// <summary>
    /// The tool NAMES a role's tools are exposed under, in the fixed order above.
    ///
    /// <para>Exists so <c>CreatePlanTool</c> can tell the orchestrator which tools a role has without
    /// keeping its own copy of the enum→name mapping. The names the orchestrator is TOLD about and the
    /// names a worker is actually OFFERED must be the same strings — two mappings would drift, and the
    /// failure is silent: the orchestrator plans against a tool name the worker cannot call.</para>
    /// </summary>
    public static IEnumerable<string> NamesFor(IReadOnlyList<WorkerTool> tools)
    {
        var allowed = new HashSet<WorkerTool>(tools);
        return Specs.Where(s => allowed.Contains(s.Tool)).Select(s => s.Spec.Name);
    }

    /// <summary>
    /// Builds one <see cref="ToolDefinition"/> per allowed <see cref="WorkerTool"/>, in the fixed
    /// order above. Empty (never null) when <paramref name="tools"/> is empty, so a call site needs
    /// no null check — both OpenAiWire and AnthropicWire gate on Count &gt; 0 and omit the `tools`
    /// key entirely for an empty list, so the two are identical on the wire anyway.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> For(IReadOnlyList<WorkerTool> tools, PluginRegistry plugins)
    {
        var allowed = new HashSet<WorkerTool>(tools);
        var result = new List<ToolDefinition>();
        foreach (var (tool, spec) in Specs)
        {
            if (!allowed.Contains(tool)) continue;
            if (!plugins.TryGet(spec.PluginType, out var plugin) || plugin is null) continue;
            result.Add(BuildDefinition(spec, plugin.GetSchema()));
        }
        return result;
    }

    private static ToolDefinition BuildDefinition(ToolSpec spec, JobSchema schema)
    {
        var byName = schema.Params.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var properties = new Dictionary<string, object>();

        foreach (var name in spec.Params)
        {
            // Throws rather than skipping: a param this tool advertises but the plugin does not
            // accept is exactly the drift generating-from-schema exists to prevent, and a silent
            // skip would ship a tool missing the parameter it needs.
            if (!byName.TryGetValue(name, out var p))
                throw new InvalidOperationException(
                    $"tool '{spec.Name}' names param '{name}', which plugin '{spec.PluginType}' "
                    + "does not accept.");

            properties[name] = new { type = p.Type, description = p.Description };
        }

        var jsonSchema = new
        {
            type = "object",
            properties,
            required = spec.Required,
        };

        var description = spec.PinnedAction is null
            ? schema.DisplayName
            : $"{schema.DisplayName} ({spec.PinnedAction})";

        return new ToolDefinition(spec.Name, description, JsonSerializer.SerializeToElement(jsonSchema));
    }

    /// <summary>
    /// Dispatches a model-issued <see cref="ToolCall"/> to its plugin and renders the result as text
    /// for a tool-result message. Never throws: every failure mode (unknown/refused tool, invalid
    /// params, a plugin exception) becomes a string the model can read and react to.
    ///
    /// <para>Order matters: (1) allow-check against <paramref name="allowed"/> BEFORE anything else —
    /// a refused tool must never reach a plugin, since a model can emit a call for a tool it was
    /// never shown; (2) <see cref="IJobPlugin.Validate"/> before execute, both as a crash guard (some
    /// plugins read required params before their own try/catch) and so the model gets a specific
    /// "'path' is required" instead of an opaque failure; (3) execute inside try/catch, since Validate
    /// does not cover I/O failures; (4) render and truncate.</para>
    /// </summary>
    public static async Task<string> InvokeAsync(ToolCall call, IReadOnlyList<WorkerTool> allowed,
        PluginRegistry plugins, IJobContext ctx, CancellationToken ct)
    {
        // Three DIFFERENT conditions, three different messages. The text goes back to the model as a
        // tool result and is the only thing it can act on: "no such tool" should make it pick a real
        // one, whereas a role refusal should make it STOP asking rather than retry variations. A
        // shared string invites exactly that retry loop, and burns turns against the cap.
        var entry = Specs.FirstOrDefault(s => s.Spec.Name == call.Name);
        if (entry.Spec is null)
            return $"no such tool '{call.Name}'. Available to this role: "
                + $"{string.Join(", ", NamesFor(allowed))}";

        if (!allowed.Contains(entry.Tool))
            return $"tool '{call.Name}' is not available to this role. Available: "
                + $"{string.Join(", ", NamesFor(allowed))}";

        // NOT a role restriction — the plugin backing this tool is missing from the registry, which
        // is a configuration fault. Saying "not available to this role" here would send whoever reads
        // it hunting through the role system for what is actually a registry problem.
        if (!plugins.TryGet(entry.Spec.PluginType, out var plugin) || plugin is null)
            return $"tool '{call.Name}' is unavailable: no '{entry.Spec.PluginType}' plugin is registered";

        var values = new Dictionary<string, object?>();
        foreach (var prop in call.Arguments.EnumerateObject())
            values[prop.Name] = prop.Value;
        if (entry.Spec.PinnedAction is not null)
            values["action"] = entry.Spec.PinnedAction;
        var parameters = new JobParameters(values);

        var validation = plugin.Validate(parameters);
        if (!validation.IsValid)
            return string.Join("; ", validation.Errors);

        // Tell the UI what this worker is DOING, before the call rather than after: a read of a large
        // file or a slow shell command is exactly when the user is staring at a silent "running…"
        // wondering whether anything is happening. Reported after validation, so a malformed call the
        // model will be asked to correct does not appear as work that happened.
        ctx.ReportToolCall(call.Name, DescribeCall(call.Name, parameters));

        JobResult result;
        try
        {
            result = await plugin.ExecuteAsync(parameters, ctx, ct);
        }
        catch (Exception ex)
        {
            return Truncate($"error: {ex.Message}", MaxToolResultChars);
        }

        var body = result.Success
            ? JobDigest.RenderOutput(result.Output)
            : $"error: {result.ErrorMessage}\n{JobDigest.RenderOutput(result.Output)}".TrimEnd();

        // Tell a worker whose read was cut HOW TO GET THE REST. The cap alone produced a loop: the
        // model re-issued the identical read_file call and got the identical elision, until the turn
        // cap killed the job. Elision is only a dead end when the result does not say what to do
        // next, and the model cannot see the tool schema's offset/limit text at the moment it is
        // staring at a hole. Appended AFTER truncation so the advice itself is never elided.
        var truncated = Truncate(body, MaxToolResultChars);
        if (truncated.Length != body.Length && call.Name == "read_file")
            truncated += "\n\nThis file was too large to return whole. Re-read it in pieces with the "
                + "'offset' (1-based line) and 'limit' (line count) parameters — see 'total_lines' "
                + "above for how far it goes. Do NOT repeat this call unchanged; it returns the same "
                + "elision.";
        return truncated;
    }

    /// <summary>Head+tail truncation with a visible elision marker — same convention as
    /// <see cref="JobDigest"/>, so the model can never mistake a cut result for the whole thing.</summary>
    /// <summary>
    /// A short human phrase for a tool call — "read Calc.cs", not the tool's RESULT (which can be
    /// thousands of characters and already reaches the transcript through the job's own output).
    /// Falls back to the bare tool name when the interesting parameter is absent, rather than
    /// inventing one.
    /// </summary>
    private static string DescribeCall(string toolName, JobParameters parameters)
    {
        // Read defensively: JobParameters.Get<T> indexes and THROWS on a missing key, and a model
        // can emit a call with any subset of params. A description is telemetry — it must never be
        // the thing that kills a job.
        string? Param(string key) =>
            parameters.Values.TryGetValue(key, out var v) ? v?.ToString() : null;

        var detail = Param("path") ?? Param("command") ?? Param("url");
        return string.IsNullOrWhiteSpace(detail) ? toolName : $"{toolName}: {detail}";
    }

    private static string Truncate(string text, int cap)
    {
        if (cap <= 0 || text.Length <= cap) return text;

        var half = cap / 2;
        var elided = text.Length - (half * 2);
        if (elided <= 0) return text;

        return text[..half] + $"\n[... {elided:N0} bytes elided ...]\n" + text[^half..];
    }
}
