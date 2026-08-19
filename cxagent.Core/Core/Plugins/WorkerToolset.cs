using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Execution;

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
    /// <summary>
    /// Cap on a tool-result string. Unbounded output would be fed into every subsequent ChatAsync
    /// call for the rest of the tool loop, so SOME bound is real — but 8,192 was far too tight and
    /// the cost was measured, not theoretical.
    ///
    /// <para>MarkupParser.cs is 1,587 lines. At 8 KB a read returns roughly a quarter of it, so
    /// finding a function meant paging blind through four windows. Across three drives the model
    /// opened that file 3, 20 and 13 times and never once landed on line 1196, where the bug was —
    /// while writing a correct description of the bug from the endpoints it COULD see. Twenty reads
    /// that fail to find one function is not a model failing to understand; it is a window too small
    /// to look through.</para>
    ///
    /// <para>64 KB holds that file whole. A modern context is 128k tokens and up, where a 64 KB read
    /// is a few percent — the old number was sized for a budget nobody runs any more. Paging still
    /// exists for genuinely huge files; it is no longer the common case for an ordinary source
    /// file.</para>
    /// </summary>
    public const int MaxToolResultChars = 65536;

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
    /// <param name="Pinned">
    /// Parameter values this tool always sends, whatever the model said.
    ///
    /// <para>The action is the usual one — <c>read_file</c> is the file plugin with
    /// <c>action=read</c> — but not the only one. <c>web_fetch</c> is the http plugin with
    /// <c>as_text=true</c>: the same plugin, the same request, a different treatment of the
    /// response. Pinning it here rather than adding a plugin keeps one HTTP implementation with one
    /// set of validation, retry and header rules.</para>
    /// </param>
    /// <param name="Description">
    /// What the model is told this tool is for. Defaults to the plugin's DisplayName, which is right
    /// while one plugin backs one tool and useless the moment two share it.
    /// </param>
    private sealed record ToolSpec(string Name, string PluginType, string? PinnedAction,
        string[] Params, string[] Required,
        IReadOnlyDictionary<string, object?>? Pinned = null,
        string? Description = null,
        ToolCrossReference? SeeAlso = null);

    /// <summary>
    /// One sentence of a description that names ANOTHER tool, kept separate so it can be dropped
    /// when that tool is not offered.
    ///
    /// <para>A pointer is only advice if the model can follow it. "Use replace_in_file instead" is
    /// the best line in write_file's description for an agent that has both, and a turn wasted for
    /// an agent that has one — the tool is called, refused as not available, and the model is back
    /// where it started with less context budget.</para>
    ///
    /// <para>ONLY FOR POINTERS AT A TOOL. A description that names a SHELL COMMAND (grep's "rather
    /// than run_shell with grep or rg") is describing what this tool replaces, not routing anywhere,
    /// and stays whole.</para>
    /// </summary>
    private sealed record ToolCrossReference(WorkerTool Tool, string Sentence);

    /// <summary>
    /// Whether a spec answers to this name.
    ///
    /// <para>NO ALIASES. These tools carried former names (<c>list_files</c>, <c>search_files</c>)
    /// and answered to them for six days after the 2026-08-13 rename, so a conversation resumed
    /// across the change would not fail on a name it had seen. That window is closed and the
    /// acceptance is gone — one name per tool, which is what the "no such tool" message can then
    /// state without qualification.</para>
    ///
    /// <para>THIS IS ONLY CHEAP BECAUSE NOTHING IS PUBLISHED. Once CxAgent.Core is on nuget.org a
    /// rename needs the acceptance window again, for the same reason it needed one here: a resumed
    /// conversation replays old tool names out of its own history, and an unknown tool costs a turn
    /// to recover from. Do not read this as "aliases were unnecessary".</para>
    /// </summary>
    private static bool Answers(ToolSpec? spec, string name) =>
        spec is not null && string.Equals(spec.Name, name, StringComparison.Ordinal);

    // Ordered so WorkerTool.For's output is stable regardless of the caller's list order —
    // a stable tool order keeps the prompt (and provider-side caching of it) stable across calls.
    private static readonly IReadOnlyList<(WorkerTool Tool, ToolSpec Spec)> Specs = new[]
    {
        (WorkerTool.ReadFile, new ToolSpec("read_file", "file", "read",
            Params: ["path", "offset", "limit"], Required: ["path"])),
        // NAMED FOR WHAT THE MODEL ALREADY KNOWS. These were list_files and search_files — accurate
        // names that describe the operation, and the model shelled out to `find` and `grep` anyway.
        // It reaches for the word it has seen a million times, which is the same reason load_skill
        // lost out to read_file until the prompt said otherwise. Cheaper to match the instinct than
        // to argue with it.
        //
        // AND SHELLING OUT COSTS MORE THAN A NAME. Through run_shell these are commands that raise a
        // permission prompt for an operation reading nothing the agent could not already read — live
        // drives stalled repeatedly on exactly those approvals.
        // PATTERN FIRST AND REQUIRED, path optional — opencode's shape, and the reason it does not
        // suffer this bug. The required argument should be the one the model is already thinking
        // about ("find the .cs files"); making `path` the required one meant a model that wanted
        // exactly that had to fill in a directory it did not care about, and put its pattern in the
        // only required slot it had been given. Order matters too: Params is documented as "the
        // order the model should read them".
        (WorkerTool.ListFiles, new ToolSpec("glob", "file", "list",
            Params: ["pattern", "path", "limit"], Required: ["pattern"],
            // SAY WHICH ARGUMENT IS WHICH. This read "Find files by path pattern, e.g. **/*.cs"
            // with `path` as the only required param, so the glob looks like it belongs in `path` —
            // and a live drive made exactly that call five times in one turn:
            //   glob {"pattern": "*cli*", "path": "**/*"}
            // Every one returned nothing, the agent fell back to `ls -R`, drowned in bin/ and obj/,
            // and reported to its planner that the project consists of DLLs. The planner then
            // correctly said it had no source to plan against. One inverted call cost the run.
            //
            // The old wording is not wrong so much as silent on the split: `path` is the DIRECTORY
            // to search under, `pattern` is the glob, and the example given was a pattern while the
            // required param was the path.
            Description: "Find files under a DIRECTORY. `path` is the folder to search "
                       + "(e.g. \".\" or \"src\"); `pattern` is the glob matched against the file "
                       + "names beneath it (e.g. \"*.cs\", or \"**/*.cs\" to recurse), defaulting to "
                       + "\"*\". The glob goes in `pattern`, never in `path`. Use this rather than "
                       + "run_shell with find or ls — it needs no approval.")),
        // Same inversion as glob, and for the same reason: the pattern is the request, the path is
        // an optional narrowing. grep already required BOTH, so it never produced the inverted call
        // — but requiring a directory the model has no opinion about is friction on every search.
        (WorkerTool.SearchFiles, new ToolSpec("grep", "file", "search",
            Params: ["pattern", "path", "regex", "glob", "limit"], Required: ["pattern"],
            Description: "Search file CONTENTS for text or a regex, optionally restricted to files "
                       + "matching a glob. Use this rather than run_shell with grep or rg — it "
                       + "needs no approval.")),
        // NO DESCRIPTION AT ALL until now, so the model was told this tool is a "File Operation" —
        // the plugin's DisplayName, which serves six actions. The one tool that can destroy work
        // silently was the one with nothing said about it.
        //
        // MOST OF IT IS WHEN NOT TO, the same shape as the spawn tool's. Overwriting is the failure
        // that costs the most and announces itself the least: the write succeeds, the tool reports
        // success, and the content that was there is simply gone.
        (WorkerTool.WriteFile, new ToolSpec("write_file", "file", "write",
            Params: ["path", "content"], Required: ["path", "content"],
            Description: "Write a whole file, creating it or REPLACING everything in it. Parent "
                       + "directories are created for you. If you do overwrite an "
                       + "existing file, read it first: what it currently holds is the only thing "
                       + "that tells you whether replacing it is what you meant. The result says "
                       + "whether it created or overwrote.",
            // ONLY IF THE MODEL HAS replace_in_file. Routing it to a withheld tool costs a turn and
            // teaches it nothing: the advice is good, and unreachable.
            SeeAlso: new(WorkerTool.ReplaceInFile,
                "To change part of a file that already exists, use replace_in_file instead — a "
                + "whole-file write means reproducing every line you are not changing, and any one "
                + "of them you misremember is a silent edit nobody asked for."))),
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
        // THE SAME PLUGIN, READING RATHER THAN CALLING. http_request hands back what the server
        // sent, which is right for an API and ruinous for a page: raw HTML is nearly all markup, a
        // tool result is re-sent every later turn, and ten fetches measured at 200k of context.
        // web_fetch pins as_text, so a page costs roughly what its words cost.
        //
        // A SEPARATE TOOL RATHER THAN A PARAMETER, because the model must choose between them by
        // INTENT — "read this page" against "call this endpoint" — and a boolean on one tool is a
        // decision it can forget to make. The names say which is which.
        (WorkerTool.WebFetch, new ToolSpec("web_fetch", "http", null,
            Params: ["url"], Required: ["url"],
            Pinned: new Dictionary<string, object?> { ["as_text"] = true },
            Description: "Read a web page as text. Fetches the URL and strips the markup, scripts, "
                       + "styles and navigation, leaving the readable content. Use this for "
                       + "documentation and articles.",
            SeeAlso: new(WorkerTool.HttpRequest,
                "Use http_request for APIs, where the raw response is what you want."))),
    };

    /// <summary>
    /// The tool NAMES, in the fixed order above.
    ///
    /// <para>Exists so a caller can name the available tools without
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
    /// The enum members whose WIRE NAMES appear in <paramref name="names"/>.
    ///
    /// <para>The inverse of <see cref="NamesFor"/>, and it lives here for the same reason that does:
    /// this type owns the enum-to-name mapping, and a second copy would drift the moment either
    /// moves. <c>WorkerTool.ListFiles</c> is offered as <c>glob</c>, so a caller that mapped by
    /// enum spelling would select nothing — the exact mistake a tool selection must not make.</para>
    ///
    /// <para>Used to narrow the DISPATCH list from a selection that was applied to definitions: the
    /// offer site and the dispatch site must agree, and deriving one from the other is what makes
    /// them agree by construction rather than by review.</para>
    /// </summary>
    public static IReadOnlyList<WorkerTool> ToolsNamed(IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        return [.. Specs.Where(s => wanted.Contains(s.Spec.Name)).Select(s => s.Tool)];
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
            result.Add(BuildDefinition(spec, plugin.GetSchema(), allowed));
        }
        return result;
    }

    private static ToolDefinition BuildDefinition(ToolSpec spec, JobSchema schema,
        IReadOnlySet<WorkerTool> allowed)
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

        // THE SPEC'S OWN WORDS WIN. Two tools can share a plugin — web_fetch and http_request are
        // both "http" — and the plugin's DisplayName then describes both identically, leaving the
        // model to choose between them on the tool name alone. That is exactly how load_skill lost
        // out to read_file on a live drive: the model reaches for what it understands, and a
        // description that does not distinguish the two is not doing its job.
        var description = spec.Description
            ?? (spec.PinnedAction is null
                ? schema.DisplayName
                : $"{schema.DisplayName} ({spec.PinnedAction})");

        // THE CROSS-REFERENCE IS CONDITIONAL, and this is the only place that can know. A
        // description naming another tool is written once and read every turn, so before the
        // selection existed it was simply true; now "use replace_in_file instead" can name a tool
        // this agent was never offered, which spends a turn to discover.
        //
        // NOT EVERY MENTION IS ONE OF THESE. grep and glob point AWAY from run_shell — toward the
        // tool being described — so they stay correct however the set is narrowed and carry no
        // SeeAlso.
        if (spec.SeeAlso is { } also && allowed.Contains(also.Tool))
            description += " " + also.Sentence;

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
    /// <param name="alsoAvailable">
    /// Tool names that exist but are not in this table — today, MCP tools.
    ///
    /// <para>Without it the unknown-tool message below lists the built-ins only, so a model that
    /// mis-typed an MCP tool is told the available tools are the seven built-ins, hiding every live
    /// MCP tool. It bites hardest after a RESUME: the restored context is replayed verbatim, so a
    /// model that used <c>fs_read</c> last session will call it again, and if that server was since
    /// removed it gets a list omitting the servers still running.</para>
    /// </param>
    public static async Task<string> InvokeAsync(ToolCall call, IReadOnlyList<WorkerTool> allowed,
        PluginRegistry plugins, IJobContext ctx, CancellationToken ct,
        IEnumerable<string>? alsoAvailable = null)
    {
        // Three DIFFERENT conditions, three different messages. The text goes back to the model as a
        // tool result and is the only thing it can act on: "no such tool" should make it pick a real
        // one, whereas a configuration fault should make it STOP asking rather than retry
        // variations. A shared string invites exactly that retry loop, and burns turns against the cap.
        var entry = Specs.FirstOrDefault(s => Answers(s.Spec, call.Name));
        if (entry.Spec is null)
            return $"no such tool '{call.Name}'. Available: "
                + $"{string.Join(", ", NamesFor(allowed).Concat(alsoAvailable ?? []))}";

        // THE ENFORCEMENT POINT, and it stays even though roles are gone. `allowed` is every tool at
        // today's only call site, so this cannot fire in production — but a model can emit a call for
        // a tool it was never shown, and this is what refuses it rather than letting an un-offered
        // tool run. Removing it because the current caller happens to pass everything would delete a
        // guard on the grounds that nothing is currently exercising it.
        //
        // The WORDING no longer says "role": that mechanism is gone, and the phrase would send
        // whoever read it hunting for a system that does not exist.
        if (!allowed.Contains(entry.Tool))
            return $"tool '{call.Name}' is not available. Available: "
                + $"{string.Join(", ", NamesFor(allowed))}";

        // A missing plugin is a CONFIGURATION fault, not a restriction, and says so.
        if (!plugins.TryGet(entry.Spec.PluginType, out var plugin) || plugin is null)
            return $"tool '{call.Name}' is unavailable: no '{entry.Spec.PluginType}' plugin is registered";

        var values = new Dictionary<string, object?>();
        foreach (var prop in call.Arguments.EnumerateObject())
            values[prop.Name] = prop.Value;
        if (entry.Spec.PinnedAction is not null)
            values["action"] = entry.Spec.PinnedAction;

        // AFTER the model's own arguments, so a pinned value cannot be talked out of. web_fetch is
        // web_fetch even if the model sends as_text:false.
        if (entry.Spec.Pinned is not null)
            foreach (var (key, value) in entry.Spec.Pinned)
                values[key] = value;
        var parameters = new JobParameters(values);

        // Validate READS the parameters, so a type slip throws HERE, before the try/catch below.
        // `run_shell {"command": ["ls","-l"]}` — argv-array form, which many shell tools do take —
        // threw a JsonException straight out of InvokeAsync, past both call sites (Agent
        // and LlmAgentJobPlugin, neither of which guards it) and killed the whole turn over one
        // correctable argument. That directly contradicts this method's "never throws" contract.
        JobValidation validation;
        try
        {
            validation = plugin.Validate(parameters);
        }
        catch (Exception ex)
        {
            return Truncate(DescribeBadArguments(call, ex), MaxToolResultChars);
        }

        if (!validation.IsValid)
            return string.Join("; ", validation.Errors)
                 + UnknownArgumentNote.For(call.Arguments.EnumerateObject().Select(p => p.Name),
                       entry.Spec.Params, call.Name);

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
        catch (System.Text.Json.JsonException ex)
        {
            // Same treatment as Validate's: name the argument, not the JSON path.
            return Truncate(DescribeBadArguments(call, ex), MaxToolResultChars);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // CANCELLATION IS NOT A TOOL RESULT. The catch-all below would turn Escape into the
            // string "error: The operation was canceled", hand it back as though the tool had
            // answered, and let the model reason about it and keep looping — the user pressed stop
            // and the turn carried on.
            //
            // It also made Agent.InvokeAndShowAsync's cancellation guard unreachable for built-ins:
            // nothing threw, so the row closed as Failed rather than Cancelled and the two paths
            // (built-in, MCP) disagreed about what stopping looks like.
            //
            // GUARDED ON ct: an OCE when cancellation was NOT requested is a plugin's own timeout or
            // a bug, and that IS a tool failure the model should see — it falls through below.
            throw;
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
        if (truncated.Length != body.Length) truncated += RecoveryAdviceFor(call.Name);
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

    /// <summary>
    /// Turns a type-conversion failure into a message naming the ARGUMENT that is wrong.
    ///
    /// <para>System.Text.Json reports position, not meaning: "The JSON value could not be converted
    /// to System.String. Path: $ | LineNumber: 0 | BytePositionInLine: 1". Handed that, a model
    /// knows something about its call was malformed but not WHICH parameter, so its cheapest move is
    /// to retry the same shape. Naming the offending arguments and showing the kinds actually sent
    /// makes the mistake correctable in one turn.</para>
    /// </summary>
    private static string DescribeBadArguments(ToolCall call, Exception ex)
    {
        var entry = Specs.FirstOrDefault(s => Answers(s.Spec, call.Name));
        var expected = entry.Spec?.Params ?? [];

        var sent = new List<string>();
        try
        {
            foreach (var prop in call.Arguments.EnumerateObject())
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    sent.Add($"'{prop.Name}' was sent as a JSON {prop.Value.ValueKind.ToString().ToLowerInvariant()}");
        }
        catch (Exception) { /* arguments themselves unreadable; fall through to the generic text */ }

        var detail = sent.Count > 0
            ? string.Join("; ", sent) + ". Each argument must be a plain scalar (string, number or "
              + "boolean), not a nested object or array."
            : $"one of the arguments has the wrong type ({ex.Message})";

        return $"error: {call.Name} could not read its arguments: {detail} "
             + $"Accepted arguments: {string.Join(", ", expected)}.";
    }

    /// <summary>
    /// How to get the part that was cut, PER TOOL.
    ///
    /// <para>This advice used to be gated on <c>read_file</c>, leaving every other tool to show a
    /// hole with no way out — the exact re-issue loop the advice exists to prevent, unfixed for the
    /// tools that need it most. The distinction that matters is whether the tool can page at all:
    /// <c>grep</c> and <c>glob</c> take a <c>limit</c>, while <c>run_shell</c> and
    /// <c>http_request</c> expose nothing, so telling those two to "narrow the parameters" would be
    /// advice they cannot follow. They are told to narrow the COMMAND instead.</para>
    /// </summary>
    private static string RecoveryAdviceFor(string toolName) => toolName switch
    {
        "read_file" =>
            "\n\nThis file was too large to return whole. Re-read it in pieces with the 'offset' "
            + "(1-based line) and 'limit' (line count) parameters — see 'total_lines' above for how "
            + "far it goes. Do NOT repeat this call unchanged; it returns the same elision.",

        // THE ADVERTISED NAMES. These read "search_files" or "list_files" — the pre-rename spellings
        // — so after 2026-08-13 neither branch could ever match and a truncated glob or grep got no
        // advice at all. Found by deleting the aliases: the old names had one live reader left.
        "grep" or "glob" =>
            "\n\nToo many results to return whole. Narrow the search — a more specific 'pattern', a "
            + "'glob' that restricts the file types, or a path further down the tree — or set a "
            + "smaller 'limit' and work through it. Do NOT repeat this call unchanged.",

        "run_shell" =>
            "\n\nThe output was too large to return whole, and this tool cannot page. Re-run with "
            + "the command itself narrowed — pipe through 'head', 'tail', 'grep', or 'wc -l' — "
            + "rather than repeating the call unchanged.",

        _ =>
            "\n\nThe result was too large to return whole and the middle was elided. Request less "
            + "in a single call rather than repeating this one unchanged.",
    };

    /// <summary>
    /// Elides the middle of an over-long result, keeping both ends.
    ///
    /// <para>The marker is counted INSIDE the cap. It was not: <c>half + marker + half</c> returned
    /// roughly <c>cap + 30</c> characters, so the one number this constant exists to guarantee was
    /// the one thing it did not — and ProcessRunner documents its own cap as matching this one.</para>
    /// </summary>
    private static string Truncate(string text, int cap)
    {
        if (cap <= 0 || text.Length <= cap) return text;

        // Measured with the real elided count, so the budget is right rather than approximately
        // right; a few characters either way in the count itself cannot push it back over.
        var marker = $"\n[... {text.Length - cap:N0} bytes elided ...]\n";
        var keep = cap - marker.Length;
        if (keep <= 0) return text[..cap];

        var half = keep / 2;
        return text[..half] + marker + text[^(keep - half)..];
    }
}
