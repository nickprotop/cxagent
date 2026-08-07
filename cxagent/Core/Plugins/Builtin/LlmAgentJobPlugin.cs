using System.Text;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins.Builtin;

/// <summary>
/// Dispatches a job to an LLM worker under a named role. This is the plugin that turns a role from
/// configuration into execution — until it existed, no job could invoke a model at all.
///
/// <para>The worker is a FRESH context that has never seen the role catalog, the goal, or the other
/// jobs. Everything it needs must be assembled here: the role's identity and behaviour as the system
/// message (via <see cref="ResolvedRole.ComposeSystemMessage"/>), and every dependency's output
/// prepended to the user turn. A synthesize job whose dependencies are not injected is asked to
/// "summarise these three reviews" with no reviews attached, and answers confidently about nothing.</para>
/// </summary>
public class LlmAgentJobPlugin : IJobPlugin
{
    /// <summary>
    /// Per-dependency budget for injected output, used only when a <c>perJobCap</c> is passed
    /// explicitly (tests pin the elision-marker behaviour this way). Production calls no longer use
    /// this default — see <see cref="DefaultTotalBudget"/>.
    /// </summary>
    private const int DefaultPerJobCap = 2048;

    /// <summary>
    /// TOTAL budget for the whole dependency block, shared across every dependency rather than sliced
    /// per item. A worker with ONE dependency may use the whole budget; a worker with many splits it
    /// newest-first, so the most recently produced (likeliest-relevant) output keeps the most bytes.
    ///
    /// <para>Replaces the old <c>DefaultPerJobCap</c> as the production default. That per-item cap was
    /// the same blunt instrument the orchestrator had one layer up (P10 Tasks 1-3): it truncated a
    /// worker's ONLY dependency to 2 KB even when nothing else competed for the budget, and let 30
    /// small dependencies pass through unbounded in aggregate. Sized generously — one dependency
    /// commonly IS the whole task ("summarise this file") — while still bounding a large fan-in.</para>
    /// </summary>
    private const int DefaultTotalBudget = 40_000;

    /// <summary>Tool-call arguments are logged, not sent to a model — a tighter budget suffices.</summary>
    private const int ToolArgLogCap = 512;

    private readonly ProviderRegistry _providers_;

    /// <summary>Every worker gets every tool. Roles used to slice this per name; they are gone.</summary>
    private static readonly IReadOnlyList<WorkerTool> AllWorkerTools =
        Enum.GetValues<WorkerTool>();
    private readonly PluginRegistry? _plugins;
    private readonly int _maxWorkerTurns;
    private readonly Action<LlmUsage>? _onUsage;

    /// <param name="plugins">
    /// The registry <see cref="WorkerToolset"/> dispatches tool calls through. NULL means "offer no
    /// tools at all" — exactly one provider call with <c>tools: null</c>, the behaviour this plugin
    /// had before the tool loop existed. That is not a degenerate case to tidy away: over twenty
    /// call sites construct this plugin without a registry, and the registry that WOULD be passed is
    /// the one holding this very plugin, so the null path also breaks the construction cycle.
    /// </param>
    /// <param name="maxWorkerTurns">
    /// Hard cap on provider round-trips for one job. Hitting it is normally a SUCCESS with
    /// <c>Output["truncated"] = true</c> — a reviewer cut off at the cap still returns most of a
    /// review. The exception is a worker that HAD write tools and used none: it was asked to change
    /// something and did not, so that is a failure. See ExecuteAsync.
    /// </param>
    /// <param name="onUsage">
    /// Invoked with EVERY turn's token usage, not just the last. A tool-using worker makes several
    /// paid calls per job; recording only the final one under-counts the goal budget by however many
    /// turns it took. Same shape as JobDiagnoser's — null (the default) simply tracks nothing.
    /// </param>
    public LlmAgentJobPlugin(ProviderRegistry providers, PluginRegistry? plugins = null,
        int maxWorkerTurns = 200, Action<LlmUsage>? onUsage = null)
    {
        _providers_ = providers;
        _plugins = plugins;
        _maxWorkerTurns = maxWorkerTurns;
        _onUsage = onUsage;
    }

    public string TypeName => "llm_agent";
    public string DisplayName => "LLM Agent";

    public JobSchema GetSchema() => new(TypeName, DisplayName, new[]
    {
        new JobParamSpec("prompt", "string", Required: true,
            "What this worker should do, in full. It sees ONLY this text — not the goal, not the other jobs.\n"
            + "\n"
            + "A worker has read_file, write_file, replace_in_file, list_files, search_files, run_shell "
            + "and http_request, and it uses them in ONE context. That is what makes it the right job "
            + "type for anything that must LOOK before it acts: an edit that has to match a file's "
            + "actual bytes, a change decided by reading code, any read-then-modify. Say what to change "
            + "and how to check it worked; the worker reads the file itself.\n"
            + "\n"
            + "Do NOT plan a separate `file read` job feeding an edit. Job outputs reach you as "
            + "DIGESTS, and a digest cannot reproduce exact bytes — a pattern written from one will "
            + "not match the file."),
        // Names the configured instances AND their models for the same reason the role param names
        // roles: an orchestrator hinting from memory invents plausible ids that resolve to nothing.
        new JobParamSpec("model_hint", "string", Required: false,
            "Optional. Preferred model, as 'instance/model' or a bare model id. Overrides the role's "
            + $"binding for this job only. Configured instances: {KnownInstances()}."),
    });

    /// <summary>
    /// Whether this job's prompt asks the worker to CHANGE something, as opposed to inspect or
    /// explain it.
    ///
    /// <para>Deliberately conservative — it decides whether "wrote nothing" is a failure, and the
    /// cost of a false positive is failing a review job that correctly produced only text. So it
    /// fires only on verbs that plainly mean mutation, and a prompt that merely mentions a file is
    /// not enough.</para>
    ///
    /// <para>A heuristic over the prompt is not where this belongs long term; the job should say
    /// what it produces. It is here because the alternative measured worse: a worker reporting done
    /// having changed nothing, which the orchestrator then reports as a completed goal.</para>
    /// </summary>
    private static bool AsksForAChange(string prompt)
    {
        ReadOnlySpan<string> verbs =
        [
            "edit ", "modify ", "change ", "add a ", "add an ", "add the ", "insert ",
            "replace ", "rewrite ", "fix ", "apply ", "update ", "remove ", "delete ",
            "write ", "create ", "implement ", "refactor ", "rename ",
        ];

        foreach (var v in verbs)
            if (prompt.Contains(v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The process working directory, which is the boundary the permission policy is built
    /// from. Null if it cannot be read — a worker with no cwd line is exactly today's behaviour, so
    /// failing to read it must never fail the job.</summary>
    private static string? TryGetWorkingDirectory()
    {
        try { return Directory.GetCurrentDirectory(); }
        catch (Exception) { return null; }
    }

    /// <summary>Each configured instance with its current model, for the model_hint description.</summary>
    private string KnownInstances()
    {
        var parts = _providers_.InstanceModels
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key} ({kv.Value})")
            .ToList();
        return parts.Count == 0 ? "(none configured)" : string.Join(", ", parts);
    }


    public JobValidation Validate(JobParameters parameters)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(parameters.Get("prompt", "")))
            errors.Add("'prompt' is required.");

        return errors.Count == 0 ? JobValidation.Valid() : JobValidation.Invalid(errors.ToArray());
    }

    /// <summary>
    /// One provider turn, streamed. Forwards each text delta to <see cref="IJobContext.ReportTextDelta"/>
    /// as it arrives, then returns the SAME <see cref="LlmResponse"/> shape the buffered call
    /// returned — so every caller downstream is unchanged.
    ///
    /// <para>Tool calls are collected and returned whole. Streaming their dispatch would change
    /// WHEN a tool runs relative to the model still generating, which is a behavioural change to
    /// the loop the permission gate runs through; showing prose sooner does not require it.</para>
    /// </summary>
    /// <summary>
    /// The text INSIDE the reasoning block — the complement of <see cref="StripReasoning"/>.
    ///
    /// <para>A reasoning model spends most of a long turn here and emits nothing else, so this is
    /// the only evidence available that it is working rather than wedged. It is shown live and
    /// discarded; it never enters the conversation, because a model that sees its own thinking
    /// replayed as content starts treating it as commitment.</para>
    ///
    /// <para>Handles the UNBALANCED case deliberately: mid-stream the opening tag has arrived and
    /// the closing one has not, which is exactly when this is wanted.</para>
    /// </summary>
    public static string ExtractReasoning(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var open = text.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return string.Empty;

        var contentStart = text.IndexOf('>', open);
        if (contentStart < 0) return string.Empty;          // tag itself still arriving
        contentStart++;

        var close = text.IndexOf("</think>", contentStart, StringComparison.OrdinalIgnoreCase);
        return close < 0
            ? text[contentStart..]                          // still thinking
            : text[contentStart..close];
    }

    /// <summary>
    /// Removes a reasoning model's <c>&lt;think&gt;…&lt;/think&gt;</c> block from generated text.
    ///
    /// <para>Seen live: a worker's finished body read literally "&lt;/think&gt;" — the reasoning
    /// tags were never stripped anywhere, so they reached the transcript AND the job's Output. The
    /// transcript is cosmetic; the output is not. <c>JobDigest</c> feeds that same text to the
    /// ORCHESTRATOR, so a downstream job consuming {{reviewer.content}} was being handed a model's
    /// private deliberation as if it were the answer.</para>
    ///
    /// <para>Handles the unbalanced case deliberately: a stream cut mid-thought, or a model that
    /// emits only the closing tag, still yields clean text rather than passing the fragment through.
    /// Text with no tags at all is returned untouched — this must not disturb the normal case.</para>
    /// </summary>
    public static string StripReasoning(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!text.Contains("<think", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("</think>", StringComparison.OrdinalIgnoreCase))
            return text;

        // Balanced blocks first.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            text, "<think[^>]*>.*?</think>", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // An unbalanced OPEN tag means everything after it is thought — drop the remainder.
        var open = cleaned.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
        if (open >= 0) cleaned = cleaned[..open];

        // An unbalanced CLOSE tag means everything before it was thought — keep the remainder.
        var close = cleaned.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (close >= 0) cleaned = cleaned[(close + "</think>".Length)..];

        return cleaned.Trim();
    }

    private static async Task<LlmResponse> StreamOneTurnAsync(ILlmProvider provider,
        List<ChatMessage> messages, List<ToolDefinition> tools, IJobContext context,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        var thinking = false;
        LlmUsage? usage = null;

        await foreach (var chunk in provider.ChatStreamAsync(
            messages, tools.Count > 0 ? tools : null, ct))
        {
            if (chunk.TextDelta is { Length: > 0 } delta)
            {
                text.Append(delta);

                // Suppress live reporting once a <think> block opens, and resume after it closes.
                // A delta-level regex cannot work — the tags arrive split across chunks — so this
                // tracks the state instead. Without it the user watches the model's private
                // deliberation stream past before the actual answer appears.
                if (delta.Contains("<think", StringComparison.OrdinalIgnoreCase)) thinking = true;
                if (!thinking) context.ReportTextDelta(delta);
                if (delta.Contains("</think>", StringComparison.OrdinalIgnoreCase)) thinking = false;
            }
            if (chunk.ToolCallDelta is { } call) toolCalls.Add(call);
            if (chunk.Usage is { } u) usage = u;
        }

        return new LlmResponse
        {
            Text = text.ToString(),
            ToolCalls = toolCalls,
            Usage = usage ?? new LlmUsage(),
        };
    }

    public async Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct)
    {
        var prompt = parameters.Get("prompt", "");
        var modelHint = parameters.Get<string?>("model_hint", null);
        var start = DateTimeOffset.UtcNow;

        // The default provider, or the one a model_hint names. Roles used to sit here, mapping a
        // name to a binding; they are gone, and what remains is the only thing that was ever load-
        // bearing — WHICH PROVIDER runs this job.
        ILlmProvider? provider = null;
        if (!string.IsNullOrWhiteSpace(modelHint))
        {
            // "instance/model" or a bare instance name. A hint that resolves to nothing is not fatal:
            // the job still runs on the default, which is better than failing over a preference.
            var instance = modelHint!.Contains('/') ? modelHint.Split('/')[0] : modelHint;
            if (_providers_.TryGet(instance, out var hinted)) provider = hinted;
            else context.Log(JobLogLevel.Warning,
                $"model_hint '{modelHint}' names no configured instance — using the default provider.");
        }

        if (provider is null && !_providers_.TryGetDefault(out provider!))
            return new JobResult
            {
                Success = false,
                ExitCode = -1,
                Duration = DateTimeOffset.UtcNow - start,
                ErrorMessage =
                    "no LLM provider is configured — set a provider and 'defaultProvider' in config.json.",
            };

        context.Log(JobLogLevel.Info,
            $"llm_agent: " +
            $"instance='{provider.ProviderId}' model='{provider.ModelId}'");

        var messages = new List<ChatMessage>();

        var system = "";

        // WHERE IT IS. A worker is a fresh context that has never seen a shell prompt, so without
        // this it does not know its own working directory — and it guesses. Measured across a
        // session's drives: of 20 run_shell calls, TEN were `find` or `ls` hunting for paths that do
        // not exist on this machine — /Users/joseph/Dev/GitHub/mimekit (the upstream author's tree,
        // straight out of training data), /home/user, and bare /. Each guess costs a permission
        // prompt and a paid turn, and the worker learns nothing from the miss.
        //
        // Appended to the role message rather than sent separately: two system messages is a shape
        // some providers handle differently, and this is one line of fact, not a second instruction.
        // One message, not two branches. With roles gone `system` is always empty, so the branch
        // that carried the "do not guess" half became unreachable — and that half is the one the
        // measurement was about: ten of twenty run_shell calls were `find`/`ls` hunting for paths
        // that do not exist on this machine.
        var cwd = TryGetWorkingDirectory();
        if (cwd is not null)
            system = $"Your working directory is {cwd}. Relative paths resolve from there. "
                   + "Do not guess absolute paths — prefer paths relative to it.";

        // Omitted ENTIRELY when empty: an empty-scaffolding system message is worse than none, since
        // it spends context asserting the worker has a role it does not have.
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new ChatMessage { Role = "system", Content = system });

        var deps = FormatDependencyOutputs(context.CompletedJobOutputs, context.CompletedJobNames,
            perJobCap: int.MaxValue, totalBudget: DefaultTotalBudget);
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = string.IsNullOrEmpty(deps) ? prompt : deps + "\n\n" + prompt,
        });

        // What this role may do, and the definitions the model is shown. Both empty when no registry
        // was supplied — a worker with no tools takes exactly one turn, as it always did.
        var allowedTools = _plugins is null
            ? Array.Empty<WorkerTool>()
            : AllWorkerTools;
        var tools = _plugins is null
            ? new List<ToolDefinition>()
            : WorkerToolset.For(allowedTools, _plugins).ToList();

        try
        {
            // Accumulated across turns, not just taken from the last response: a worker often narrates
            // ("let me check that file") on the turn it calls a tool, and that text is part of its
            // answer. Taking only the final turn's Text silently discards it.
            var transcript = new StringBuilder();

            // The planner's proposal, if it makes one. Held across turns because a planner may call
            // propose_jobs and then keep talking; the LAST proposal wins, which is what a model
            // refining its own plan expects.
            System.Text.Json.JsonElement? proposal = null;

            // Whether this worker actually CHANGED anything. A producing job that hits the turn cap
            // having only read is a job that did not do its work, however confidently it narrates.
            var wroteAnything = false;
            var truncated = false;

            for (int turn = 0; ; turn++)
            {
                // Checked each turn rather than trusting the driver to notice: an already-cancelled
                // goal must not spend a paid API call, and not every ILlmProvider observes the token
                // before its first await. Inside the loop so a cancel mid-loop stops the next call too.
                ct.ThrowIfCancellationRequested();

                // STREAMED, so the UI can show prose building instead of a spinner followed by a
                // wall of text. A 90-second reviewer previously looked identical whether it was
                // producing brilliant analysis or spinning uselessly.
                //
                // Text streams; TOOL TURNS STILL BUFFER. Deltas are accumulated and the turn is
                // dispatched only once the stream completes, exactly as the non-streaming call did —
                // so the tool loop, and the allow-check WorkerToolset runs inside it, behave
                // identically. That loop is the twin-path security closure and has already been hit
                // by four defects (D7/D8/D11/D12); it is not the place to also change dispatch timing.
                var response = await StreamOneTurnAsync(provider, messages, tools, context, ct);

                // EVERY turn, not just the last — see the _onUsage doc comment.
                _onUsage?.Invoke(response.Usage);

                var visibleText = StripReasoning(response.Text);
                if (!string.IsNullOrWhiteSpace(visibleText))
                {
                    if (transcript.Length > 0) transcript.AppendLine();
                    transcript.Append(visibleText);
                }

                // The ONLY record that a job's tool calls happened — nothing else persists them, and
                // by the time anyone asks "what is this job doing", the data would be gone.
                foreach (var call in response.ToolCalls)
                    context.Log(JobLogLevel.Info,
                        $"tool: {call.Name} {Truncate(call.Arguments.ToString(), ToolArgLogCap)}");

                // The loop exit, keyed on ToolCalls.Count and NOT on StopReason. ToolCalls is
                // initialised to new() so it is never null, and the calls are what actually has to be
                // executed — their presence IS the condition. StopReason would be the subtler bug:
                // MockLlmProvider never sets it, so a loop gated on it passes every unit test here and
                // diverges only against a live provider.
                if (response.ToolCalls.Count == 0)
                    break;

                // The cap, checked AFTER handling this turn's text/logging so the work already paid
                // for is kept, and BEFORE running the tools so we never execute a tool whose result
                // no model turn will ever read.
                if (turn + 1 >= _maxWorkerTurns)
                {
                    truncated = true;
                    context.Log(JobLogLevel.Warning,
                        $"llm_agent: stopped at the {_maxWorkerTurns}-turn limit with tool calls still pending — "
                        + "returning partial output. Split the work into smaller jobs, or raise orchestrator.maxWorkerTurns.");
                    break;
                }

                // The assistant turn that MADE the calls must precede their results, or the results
                // dangle and providers reject (or silently ignore) the conversation.
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = response.Text ?? "",
                    ToolCalls = response.ToolCalls.ToList(),
                });

                foreach (var call in response.ToolCalls)
                {

                    // Never throws — every failure mode (refused tool, invalid params, plugin
                    // exception) comes back as a string the model can read and react to.
                    var toolResult = await WorkerToolset.InvokeAsync(call, allowedTools, _plugins!, context, ct);

                    // Counted on SUCCESS, not on attempt. InvokeAsync reports a failed write as an
                    // error STRING rather than throwing, so marking the intent was marking the wrong
                    // thing: a worker whose every replace failed still looked like it had written.
                    if (call.Name is "write_file" or "replace_in_file"
                        && !toolResult.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                        && !toolResult.Contains("was not found", StringComparison.Ordinal))
                        wroteAnything = true;
                    messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        // `call.Id ?? call.Name`, never a bare call.Id. ToolCallId is the ONLY field
                        // that marks a message as a tool result: both wires branch on it and set the
                        // role themselves. A null here does not degrade the message, it turns it into
                        // an ordinary user turn — no error, no warning, the model just never sees the
                        // result. Real providers populate Id, so this bites only in tests, which is
                        // worse: the loop looks correct precisely where the bug is invisible.
                        ToolCallId = call.Id ?? call.Name,
                        Content = toolResult,
                    });
                }
            }

            var output = new Dictionary<string, object?>
            {
                ["content"] = transcript.ToString(),
                ["model"] = provider.ModelId,
            };
            // The machine-readable half of the warning above: the orchestrator reads the digest and
            // can split the work. Only set when it happened — an absent key means "ran to completion".
            if (truncated) output["truncated"] = true;

            // THE PROPOSAL. Raw JSON as the planner wrote it: ConsultJobCompiler is the thing that
            // validates a job list, and re-shaping it here would be a second, subtly different
            // compilation path. An absent key means "this job proposed nothing", which is different
            // from proposing an empty list and saying why.
            if (proposal is { } p) output["proposed_jobs"] = p.GetRawText();

            // TRUNCATED WITH NOTHING TO SHOW is a FAILURE, not a partial success. The rule below is
            // right for a reviewer — cut off at the cap it still returns most of a review — but an
            // implementer cut off before its first write returns nothing of value, and calling that
            // "done" is how a goal completes green having changed no files.
            //
            // Measured: an implementer asked to edit six files spent every turn reading them (16
            // read_file calls, zero writes) and reported done · 27.6s. The orchestrator saw two
            // green jobs and finished the goal. Raising the cap makes this rarer; it does not make
            // "I was about to" mean "I did".
            //
            // Scoped to a worker that HAD write tools and used none: a read-only role producing only
            // text is doing exactly its job.
            // NOT gated on `truncated` any more. A worker that runs out of turns having written
            // nothing is one failure; a worker that finishes NORMALLY having written nothing is the
            // same failure and was passing straight through. Measured: a job asked to guard
            // QEncoder.cs made eight replace attempts, all of them failing, then concluded "It looks
            // like the file has already been modified" -- it had seen a SIBLING job's edit to a
            // different file and credited it to itself -- and reported done in 114 seconds having
            // changed nothing.
            if (allowedTools.Contains(WorkerTool.WriteFile) && !wroteAnything && AsksForAChange(prompt))
                return new JobResult
                {
                    Success = false,
                    ExitCode = -1,
                    Duration = DateTimeOffset.UtcNow - start,
                    Output = output,
                    ErrorMessage =
                        $"hit the {_maxWorkerTurns}-turn limit before writing anything. The work was "
                        + "not done. Split it into smaller jobs — one file per job — or raise "
                        + "orchestrator.maxWorkerTurns.",
                };

            // Success even when truncated, deliberately. AppBootstrap wires onJobFailed to automatic
            // diagnosis, so reporting a hit cap as a failure would burn a diagnosis round AND a retry
            // on a limit we set on purpose, for a job that has perfectly good partial text to hand
            // back. Failure in this plugin means "no provider" or "the provider threw" — conditions
            // where there is nothing to return at all. A cap hit is different in kind.
            return new JobResult
            {
                Success = true,
                ExitCode = 0,
                Duration = DateTimeOffset.UtcNow - start,
                Output = output,
            };
        }
        // Cancellation is a user-initiated stop, not a job failure — swallowing it here would report
        // every cancelled goal as an LLM error. Rethrown for the executor to classify.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Log(JobLogLevel.Error, $"llm_agent failed: {ex.Message}");
            return new JobResult
            {
                Success = false,
                ExitCode = -1,
                Duration = DateTimeOffset.UtcNow - start,
                ErrorMessage = ex.Message,
            };
        }
    }

    /// <summary>
    /// Renders every dependency's result as a labelled block for injection into the worker's user
    /// turn, or "" when there are none — a root job must not receive a stray "Dependency results:"
    /// header promising context that does not exist.
    ///
    /// <para>Truncation policy, shared deliberately with P8's orchestrator digest: bounded head+tail
    /// per job, a VISIBLE elision marker (the model must know text was cut, or it reasons confidently
    /// about output it never saw), <see cref="JobResult.ErrorMessage"/> NEVER truncated (a truncated
    /// error is the one thing a debugger role cannot work with), and <see cref="JobResult.LogFile"/>
    /// named whenever output was cut so a follow-up job can read the whole thing.</para>
    /// </summary>
    /// <param name="displayNames">
    /// Job.Id → human-readable name, as carried by <see cref="IJobContext.CompletedJobNames"/>. Blocks
    /// are labelled with the NAME because <paramref name="outputs"/> is keyed by ULID, which is
    /// meaningless to a worker whose plan referred to jobs by name. An id with no entry here falls
    /// back to the raw key, so attribution degrades rather than disappearing.
    /// </param>
    /// <param name="perJobCap">
    /// PER-ITEM cap, used only when passed explicitly. Existing tests pin the elision-marker behaviour
    /// through this parameter; it is otherwise superseded by <paramref name="totalBudget"/> — passing
    /// both applies whichever is smaller for each block, but production code passes only the total.
    /// </param>
    /// <param name="totalBudget">
    /// TOTAL budget for every dependency body combined — the bound that actually matters, since ten
    /// dependencies under any per-item cap can still overflow the model's window together. Spent
    /// NEWEST-FIRST (by ULID, descending): the most recently produced output is the likeliest referent,
    /// so it is the one that keeps its bytes when the budget runs out. This does NOT change render
    /// order — blocks are still emitted oldest-first below, exactly as before.
    /// </param>
    public static string FormatDependencyOutputs(
        IReadOnlyDictionary<string, JobResult> outputs,
        IReadOnlyDictionary<string, string>? displayNames = null,
        int perJobCap = DefaultPerJobCap,
        int totalBudget = DefaultTotalBudget)
    {
        if (outputs.Count == 0) return "";

        // Bodies only — errors are never truncated and never compete for the shared budget (see the
        // NeverTruncatesErrors test). Rendered once here so both the allocation pass and the render
        // pass below see the same text.
        var bodies = outputs.ToDictionary(kv => kv.Key, kv => RenderOutput(kv.Value.Output));

        // Allocate newest-first: walk dependencies from most-recently-produced to oldest, handing each
        // as much of the REMAINING total budget as it can use (capped per-item too, when perJobCap was
        // passed explicitly). A dependency that fits within what's left is not truncated at all —
        // exactly the case a fixed per-item cap got wrong for a worker with only one dependency.
        var perDepCap = new Dictionary<string, int>();
        var remaining = totalBudget;
        foreach (var (jobId, body) in bodies.OrderByDescending(kv => kv.Key, StringComparer.Ordinal))
        {
            var cap = Math.Min(perJobCap, Math.Max(remaining, 0));
            perDepCap[jobId] = cap;
            remaining -= Math.Min(body.Length, cap);
        }

        var sb = new StringBuilder();
        sb.AppendLine("Dependency results:");

        // Explicitly ordered: Dictionary enumeration order is not a documented guarantee, and this
        // helper is public static over IReadOnlyDictionary — P8 will feed it a dictionary built
        // elsewhere. Without this, "summarise these three reviews in order" silently reorders between
        // runs with no error. Sorted by ULID, which is lexicographically time-ordered, so the blocks
        // read in dependency-completion order rather than an arbitrary one — deliberately UNCHANGED by
        // the newest-first allocation above, which decides WHICH bytes survive, not WHERE they render.
        foreach (var (jobId, result) in outputs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var label = displayNames is not null && displayNames.TryGetValue(jobId, out var name)
                        && !string.IsNullOrWhiteSpace(name)
                ? name
                : jobId;

            sb.AppendLine();
            sb.AppendLine($"[{label}] {(result.Success ? "Succeeded" : "Failed")}");

            // In full, always. Whether the dependency also produced output, the reason it failed is
            // the load-bearing part.
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                sb.AppendLine($"error: {result.ErrorMessage}");

            var body = bodies[jobId];
            if (!string.IsNullOrWhiteSpace(body))
            {
                // A cap of 0 here means "the shared budget ran out before reaching this dependency",
                // not Truncate's own convention where cap<=0 means "unbounded" (JobDigest and the
                // explicit-perJobCap tests both rely on that meaning elsewhere). Elide the body
                // entirely rather than let a starved dependency slip through whole.
                var cap = perDepCap[jobId];
                var cut = cap <= 0 && body.Length > 0
                    ? $"[... {body.Length:N0} bytes elided (dependency budget exhausted) ...]"
                    : Truncate(body, cap);
                sb.AppendLine(cut);
                if (cut.Length != body.Length && !string.IsNullOrWhiteSpace(result.LogFile))
                    sb.AppendLine($"(full output: {result.LogFile})");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Flattens a job's output bag to text. 'content' is rendered bare because it is the whole point
    /// of the block; every other key is labelled, since a shell job's <c>exit_code</c> means nothing
    /// unlabelled — and dropping unknown keys would render such a dependency as blank.
    /// </summary>
    private static string RenderOutput(Dictionary<string, object?> output)
    {
        if (output.Count == 0) return "";

        var parts = new List<string>();
        foreach (var (key, value) in output)
        {
            var text = value?.ToString();
            if (string.IsNullOrEmpty(text)) continue;
            parts.Add(key == "content" ? text : $"{key}: {text}");
        }
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Head+tail truncation with a visible byte count. Both ends are kept because a job's result
    /// typically states what it did at the top and concludes at the bottom; a head-only cut discards
    /// the conclusion, which is usually the part a dependent job needs.
    /// </summary>
    private static string Truncate(string text, int cap)
    {
        if (cap <= 0 || text.Length <= cap) return text;

        var half = cap / 2;
        var elided = text.Length - (half * 2);
        // A marker longer than what it saves is not a saving.
        if (elided <= 0) return text;

        return text[..half] + $"\n[... {elided:N0} bytes elided ...]\n" + text[^half..];
    }
}
