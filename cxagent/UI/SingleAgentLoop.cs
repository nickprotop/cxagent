using System.Text;
using CxAgent.Core.Llm;
using CxAgent.Core.Execution;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;

namespace CxAgent.UI;

/// <summary>
/// The whole of single-agent mode: one model, its tools, and a turn loop. No plan, no DAG, no
/// consult.
///
/// <para>WHY THIS EXISTS. The plan/drive/consult cycle asks the orchestrator to describe work it
/// cannot yet see. A `file replace` needs the target's exact bytes; those arrive only after a read
/// job FINISHES, and by then the orchestrator is being asked whether the goal is done rather than
/// what to do next. Measured across a long session: it produced a perfect edit — right tabs, right
/// house style, the exact exception asked for — and then had nowhere to put it, so it emitted the
/// edit as prose under an invented `{"action":"edit_file"}` schema and nothing was written. Another
/// drive read twelve files and reported success having changed none. The failure was never the
/// prompt; three wordings were tried. It is that describing an action and taking one were different
/// channels, and only the describing channel was open.</para>
///
/// <para>Here they are the same channel. The model calls <c>read_file</c>, sees the bytes in its own
/// context, and calls <c>replace_in_file</c> with text it is LOOKING AT. Nothing is reconstructed
/// from a digest because nothing round-trips through one.</para>
///
/// <para>PERMISSIONS ARE UNCHANGED, and that is structural rather than careful: every call goes
/// through <see cref="WorkerToolset.InvokeAsync"/> into the same <see cref="PluginRegistry"/>, whose
/// file/shell/http plugins are wrapped in <c>PermissionGatedPlugin</c>. The gate reads
/// <c>(TypeName, parameters)</c> and nothing else — no part of the job path was load-bearing for it,
/// which is what makes this substitution safe.</para>
///
/// <para>WHAT IS LOST, stated plainly: copilot's whole-plan pre-approval has no plan to approve, and
/// the DAG's parallelism is gone. Both are fan-out's job now (<c>--fan-out</c>).</para>
/// </summary>
public sealed class SingleAgentLoop
{
    private readonly ILlmProvider _provider;
    private readonly PluginRegistry _plugins;
    private readonly TokenLedger _ledger;
    private readonly IChatSink _sink;
    private readonly IJobPanel _jobs;
    private readonly LogFileManager? _logs;
    private readonly int _maxTurns;

    public SingleAgentLoop(ILlmProvider provider, PluginRegistry plugins, TokenLedger ledger,
        IChatSink sink, IJobPanel jobs, LogFileManager? logs, int maxTurns)
    {
        _provider = provider;
        _plugins = plugins;
        _ledger = ledger;
        _sink = sink;
        _jobs = jobs;
        _logs = logs;
        _maxTurns = maxTurns;
    }

    /// <summary>Every tool, always. Roles used to slice this per worker name; that mechanism is gone
    /// and safety lives in the permission gate, not in withholding capability.</summary>
    private static readonly IReadOnlyList<WorkerTool> AllTools = Enum.GetValues<WorkerTool>();

    /// <summary>
    /// Runs the goal to completion and returns its final state.
    ///
    /// <para><paramref name="conversation"/> is the SESSION's history and is appended to only twice:
    /// the user's goal (by the caller) and the final answer. The turn-by-turn working — tool calls,
    /// tool results, intermediate prose — lives on a goal-local copy and is discarded. That is the
    /// same rule the consult loop follows, and here it also avoids a concrete hazard: orphaned
    /// <c>ToolCallId</c> pairings surviving into a later goal, which providers reject and the
    /// session compressor does not understand.</para>
    /// </summary>
    public async Task<GoalState> RunAsync(string goalId, List<ChatMessage> conversation,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>(conversation);

        // WHERE IT IS. A fresh context has never seen a shell prompt, and measured across one
        // session, ten of twenty shell calls were `find`/`ls` hunting for paths that do not exist on
        // this machine — /Users/<someone>/…, /home/user, bare /.
        var cwd = TryGetWorkingDirectory();
        if (cwd is not null)
            messages.Insert(0, new ChatMessage
            {
                Role = "system",
                Content = $"Your working directory is {cwd}. Relative paths resolve from there. "
                        + "Do not guess absolute paths — prefer paths relative to it.\n\n"
                        + "You have tools. USE THEM: read a file before editing it, and make changes "
                        + "with write_file or replace_in_file rather than describing them. Text in a "
                        + "message changes nothing.",
                        // NO DEBUGGING ADVICE HERE. A paragraph on tracing a value between where it
                        // is set and where it is used lived here briefly, added after three drives
                        // failed to find one bug. It was generalised from a single case whose answer
                        // was already known, and it rode on EVERY goal — including the ones that only
                        // ask a question. The cap was the real constraint: an 8 KB window on a
                        // 1,587-line file meant the model read a quarter of it at a time, and no
                        // amount of coaching fixes a window too small to look through. Raising the
                        // window removes the problem; describing how to page around it only hides it.
                Timestamp = DateTimeOffset.UtcNow,
            });

        var tools = WorkerToolset.For(AllTools, _plugins).ToList();
        var wrote = false;
        var challenges = 0;

        // The LAST build/test result seen this goal, or null if none was ever run. Tracked because a
        // broken edit is not a finished goal, and the model will say it is: measured live, an agent
        // wrote a correct diagnosis, its patch failed to compile (`error CS1612`), the transcript
        // recorded "Build FAILED", and it reported success in the same breath. `wrote` was true, so
        // the no-write challenge never fired — the goal was broken in a way the existing gate is
        // structurally unable to see.
        string? lastBuild = null;

        // Identical (call, arguments, result) triples seen this goal, for stuck detection below.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var turn = 0; ; turn++)
        {
            ct.ThrowIfCancellationRequested();

            // AT THE CAP, ASK FOR A HANDOFF rather than discarding the run. Hitting the cap used to
            // print one line and throw away everything the model had learned — the user was left
            // with a half-edited tree and no account of what happened or what remains.
            //
            // opencode does the inverse of a keep-going nudge here: it injects a forced-stop prompt
            // ("Tools are disabled until next user input… MUST provide a text response summarizing
            // work done so far") and takes a summary. SWE-agent auto-submits whatever diff exists,
            // on the same principle — an interrupted run should still yield its artifact. Both
            // salvage; neither discards.
            //
            // The summary turn runs WITHOUT tools, so it cannot start new work, and it is the last
            // thing the loop does either way.
            if (turn >= _maxTurns)
            {
                var summary = await SummariseAtCapAsync(messages, ct);
                if (!string.IsNullOrWhiteSpace(summary))
                    conversation.Add(new ChatMessage
                        { Role = "assistant", Content = summary, Timestamp = DateTimeOffset.UtcNow });

                _sink.ShowError($"stopped after {_maxTurns} turns without finishing.");
                return GoalState.Failed;
            }

            // OPEN THE TURN BEFORE THE CALL, not after it. A turn is created with thinking:true and
            // the control clears that flag when body content arrives, so opening it here puts the
            // spinner on screen for the whole wait — which is exactly the part that takes seconds to
            // minutes on a local model.
            //
            // It used to be opened and closed together, AFTER the response had fully arrived: the
            // one moment nothing needed indicating. Between a tool result and the next response the
            // transcript sat completely still, with no way to tell a model that is thinking from one
            // that has died somewhere in the silicon.
            var turnId = _sink.BeginAssistantTurn();
            LlmResponse response;
            try
            {
                response = await StreamTurnAsync(messages, tools, ct, turnId);
            }
            catch (Exception)
            {
                // The turn MUST be closed on every path. A spinner left running after a failure is
                // worse than no spinner: it says "still working" about a goal that is already over.
                _sink.EndAssistantTurn(turnId);
                throw;
            }

            _ledger.Record(response.Usage);

            var text = Core.Plugins.Builtin.LlmAgentJobPlugin.StripReasoning(response.Text);

            // Nothing more will be appended to this turn. Closing it stops the spinner; the text (if
            // any) was streamed in as it arrived.
            _sink.EndAssistantTurn(turnId);

            if (response.ToolCalls.Count == 0)
            {
                // A turn with no tool calls is the model saying it is done. CHALLENGE IT if the goal
                // asked for a change and nothing was written — the failure this mode exists to fix
                // ends exactly here, with a confident summary of work that never happened.
                //
                // MORE THAN ONCE, and escalating. A single nudge was measured against a real bug
                // hunt: the model answered the challenge with PROSE rather than a tool call, and the
                // loop took that as done — twice in a row, 55 tool calls across two runs, nothing
                // written either time. One challenge only catches a model that forgot to write; it
                // does nothing about one that has stalled mid-investigation, which is the commoner
                // case on a hard task.
                // An explicit refusal ends it. Challenging a model that has already said it cannot
                // proceed just burns turns to hear the same thing louder.
                var refused = text.Contains("CANNOT:", StringComparison.OrdinalIgnoreCase);

                // Two ways a change goal can finish badly, and they need different words: nothing was
                // written at all, or something was written that does not build.
                var broken = wrote && lastBuild is not null && BuildFailed(lastBuild);
                var unfinished = !wrote && AsksForAChange(conversation);

                if ((unfinished || broken) && !refused && challenges < MaxChallenges)
                {
                    challenges++;
                    messages.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = broken ? BrokenBuildChallenge(lastBuild!) : ChallengeText(challenges),
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    continue;
                }

                // Already ON SCREEN — it streamed into the turn opened above. Only the session
                // conversation still needs it.
                if (!string.IsNullOrWhiteSpace(text))
                    conversation.Add(new ChatMessage
                        { Role = "assistant", Content = text, Timestamp = DateTimeOffset.UtcNow });

                // NOT Completed when the goal wanted a change and none was made. Reporting success
                // over an unchanged working tree is the same lie this mode was built to stop, one
                // level up: the run says done, the disk says otherwise, and the user finds out later.
                if (unfinished)
                {
                    _sink.ShowError(
                        "the goal asked for a change, but nothing was written. Investigation ran to "
                        + "a stop without reaching an edit.");
                    return GoalState.Failed;
                }

                // A BROKEN BUILD IS A FAILED GOAL. Measured live: a correct diagnosis, a patch that
                // did not compile, "Build FAILED" in the transcript, and a confident success summary
                // in the same turn. Edits were made, so the no-write gate above saw nothing wrong —
                // this is the one that has to catch it.
                if (broken)
                {
                    _sink.ShowError(
                        "changes were written but the build did not succeed. The last build or test "
                        + "run reported a failure and it was not resolved.");
                    return GoalState.Failed;
                }

                return GoalState.Completed;
            }

            // Prose that came WITH tool calls is narration ("let me check that file"). It has
            // already streamed into this turn; nothing more to render.

            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = response.Text ?? "",
                ToolCalls = response.ToolCalls.ToList(),
            });

            foreach (var call in response.ToolCalls)
            {
                var result = await InvokeAndShowAsync(goalId, call, ct);
                if (IsWrite(call.Name) && !LooksLikeFailure(result)) wrote = true;

                // STUCK: the same call returning the same result, over and over. Measured on one
                // drive that produced nothing in 42 calls — MarkupParser.cs was READ six times and
                // SEARCHED five times, each returning what it had already returned. A model in that
                // state is not making progress and will not spontaneously leave it; every repeat is
                // a paid turn against the cap.
                //
                // OpenHands calls this "scenario 1: same action, same observation" and nudges once
                // before killing, which is the right order — the model may simply have lost track,
                // and telling it so is far cheaper than failing the goal.
                var signature = call.Name + " " + call.Arguments.ToString() + " " + result;
                seen.TryGetValue(signature, out var times);
                seen[signature] = ++times;

                if (times == StuckRepeats)
                    messages.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = $"You have called {call.Name} with the same arguments {times} times "
                                + "and received the same result each time. Repeating it will not "
                                + "produce anything new. Use what you already have, or try a "
                                + "genuinely different approach.",
                        Timestamp = DateTimeOffset.UtcNow,
                    });

                // A build or test run REPLACES the previous verdict rather than accumulating: what
                // matters at the end is whether the tree compiles NOW, not whether it ever did. A
                // model that breaks the build, fixes it, and stops has finished the job.
                if (call.Name == "run_shell" && LooksLikeBuildOrTest(call)) lastBuild = result;

                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    // call.Id ?? call.Name, never a bare Id: ToolCallId is the ONLY field marking a
                    // message as a tool result, and a null turns it into an ordinary user turn — no
                    // error, no warning, the model simply never sees the result.
                    ToolCallId = call.Id ?? call.Name,
                    Content = result,
                });
            }
        }
    }

    /// <summary>
    /// One final tool-less turn asking what was done and what remains, shown in the transcript.
    ///
    /// <para>NO TOOLS, deliberately: the cap has been reached, so the model must not be able to
    /// start work it cannot finish. Passing an empty tool list is what makes that structural rather
    /// than a request the model may ignore — opencode has to say "any attempt to use tools is a
    /// critical violation" in its prompt precisely because its tools are still bound.</para>
    ///
    /// <para>Best-effort: a provider failure here must not replace the cap message with a stack
    /// trace, since the goal has already ended and the summary is a courtesy on top of it.</para>
    /// </summary>
    private async Task<string> SummariseAtCapAsync(List<ChatMessage> messages, CancellationToken ct)
    {
        var ask = new List<ChatMessage>(messages)
        {
            new()
            {
                Role = "user",
                Content = "You have reached the maximum number of steps for this task and no more "
                        + "tools are available. Reply with text only: what you accomplished, what "
                        + "is left unfinished, and what you would do next. Be specific about files "
                        + "you changed.",
                Timestamp = DateTimeOffset.UtcNow,
            },
        };

        var turnId = _sink.BeginAssistantTurn();
        try
        {
            var response = await StreamTurnAsync(ask, [], ct, turnId);
            _ledger.Record(response.Usage);
            return Core.Plugins.Builtin.LlmAgentJobPlugin.StripReasoning(response.Text);
        }
        catch (Exception)
        {
            return "";
        }
        finally
        {
            _sink.EndAssistantTurn(turnId);
        }
    }

    /// <summary>
    /// Dispatches one tool call and renders it as a transcript row.
    ///
    /// <para>The row is a SYNTHETIC job — it enters no scheduler and no dag. It exists because the
    /// user already reads job rows ("Tool  Read HexEncoder.cs · done · 0.0s") and a tool call is the
    /// same event; inventing a second visual language for it would be gratuitous. Without this the
    /// calls are invisible: <c>ToolCallReported</c> has no UI subscriber anywhere in the app.</para>
    /// </summary>
    private async Task<string> InvokeAndShowAsync(string goalId, ToolCall call, CancellationToken ct)
    {
        var jobId = Helpers.UlidGenerator.NewId();
        var job = new Job
        {
            Id = jobId,
            PlanLocalId = call.Name,
            GoalId = goalId,
            PluginType = ToolPluginType(call.Name),
            DisplayName = DescribeCall(call),
            State = JobState.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _jobs.SetJobs(new[] { job });

        var started = DateTimeOffset.UtcNow;
        var ctx = new JobContext(goalId, jobId, new Dictionary<string, JobResult>(), _logs);
        var result = await WorkerToolset.InvokeAsync(call, AllTools, _plugins, ctx, ct);

        var failed = LooksLikeFailure(result);
        job.State = failed ? JobState.Failed : JobState.Succeeded;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Result = new JobResult
        {
            Success = !failed,
            ExitCode = failed ? -1 : 0,
            Duration = DateTimeOffset.UtcNow - started,
            ErrorMessage = failed ? result : null,
            Output = new Dictionary<string, object?> { ["content"] = result },
        };
        _jobs.UpdateJob(job);

        return result;
    }

    private async Task<LlmResponse> StreamTurnAsync(List<ChatMessage> messages,
        List<ToolDefinition> tools, CancellationToken ct, ChatMessageId turnId)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        LlmUsage usage = new();

        // How much of the (reasoning-stripped) text has already been shown. Deltas arrive raw, and a
        // reasoning block can span many of them, so what is SAFE to display is recomputed from the
        // accumulated text after each chunk rather than appended blindly — streaming the raw delta
        // would put the model's <think> block on screen, which is exactly what StripReasoning exists
        // to prevent.
        var shown = 0;

        await foreach (var chunk in _provider.ChatStreamAsync(messages, tools, ct))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                text.Append(chunk.TextDelta);

                var visible = Core.Plugins.Builtin.LlmAgentJobPlugin.StripReasoning(text.ToString());
                if (visible.Length > shown)
                {
                    _sink.AppendAssistant(turnId, visible[shown..]);
                    shown = visible.Length;
                }
            }

            if (chunk.ToolCallDelta is { } tc) calls.Add(tc);
            if (chunk.Usage is { } u) usage = u;
        }

        return new LlmResponse { Text = text.ToString(), ToolCalls = calls, Usage = usage };
    }


    private static bool IsWrite(string toolName) =>
        toolName is "write_file" or "replace_in_file";

    /// <summary>How many times a no-write finish is challenged before the goal is failed.</summary>
    private const int MaxChallenges = 3;

    /// <summary>Identical repeats of one (call, arguments, result) before the model is told; twice
    /// that many before the goal is failed. Three is high enough that a legitimate re-read after
    /// changing something is never mistaken for a loop.</summary>
    private const int StuckRepeats = 3;

    /// <summary>
    /// The nudge sent when the model stops without writing. EACH ONE SAYS SOMETHING NEW — repeating
    /// a message the model has already answered just earns the same answer again, which is exactly
    /// what a single fixed challenge produced in measurement.
    ///
    /// <para>The escalation follows the observed failure: first assume it forgot to write, then
    /// assume it stalled mid-investigation and name the concrete recovery (widen the read — a large
    /// file read through a 40-line window is how the relevant function gets missed), then demand a
    /// decision either way so a genuine "cannot" ends the goal honestly instead of looping.</para>
    /// </summary>
    private static string ChallengeText(int attempt) => attempt switch
    {
        1 => "Nothing was written. The request asked you to change something — use write_file or "
           + "replace_in_file to do it now, or say plainly why it cannot be done.",

        2 => "Still nothing written. You stopped before reaching an edit. If you have not yet found "
           + "the cause, keep looking — read the whole of the file you suspect rather than a small "
           + "window of it (omit 'limit', or use a large one), and search for the function that sits "
           + "BETWEEN where the relevant value is set and where it is used. Then make the edit.",

        _ => "This is the final attempt. Either call replace_in_file or write_file now, or reply "
           + "with one sentence beginning 'CANNOT:' explaining what is blocking you. Do not "
           + "summarise what you have read — a summary changes nothing on disk.",
    };

    /// <summary>
    /// Whether a tool result reads as a failure. WorkerToolset never throws — every failure comes
    /// back as a STRING — so "did that write land" cannot be answered by exception handling. Matched
    /// on the two shapes the plugins actually produce.
    /// </summary>
    private static bool LooksLikeFailure(string result) =>
        result.StartsWith("error", StringComparison.OrdinalIgnoreCase)
        || result.Contains("was not found", StringComparison.Ordinal)
        || result.Contains("is required", StringComparison.Ordinal);

    /// <summary>
    /// Whether a shell call was a BUILD or TEST run — the commands whose result says whether the
    /// edits actually work.
    ///
    /// <para>Matched on the command text, which is the only signal available: run_shell is one tool
    /// and every toolchain looks different through it. Deliberately narrow — a command that is not
    /// recognised simply does not update the verdict, which fails safe (the goal is judged on the
    /// last build it DID run, or on nothing at all).</para>
    /// </summary>
    private static bool LooksLikeBuildOrTest(ToolCall call)
    {
        var cmd = TryGetArgument(call, "command");
        if (string.IsNullOrEmpty(cmd)) return false;

        ReadOnlySpan<string> verbs =
        [
            "dotnet build", "dotnet test", "msbuild",
            "cargo build", "cargo test", "cargo check",
            "go build", "go test",
            "npm run build", "npm test", "yarn build", "yarn test", "pnpm build", "pnpm test",
            "make", "cmake --build", "gradle", "mvn ", "pytest", "tsc",
        ];
        foreach (var v in verbs)
            if (cmd.Contains(v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Whether a build/test result reads as a failure.
    ///
    /// <para>Exit code would be the honest signal, but it does not survive: WorkerToolset renders a
    /// shell result as text, and a non-zero exit already arrives prefixed "error:". Both forms are
    /// matched, plus the phrases the major toolchains print, because a command that fails INSIDE a
    /// pipeline (`… | tail -30`) exits 0 and only says so in its output — which is exactly how the
    /// live failure was invisible: `dotnet build … 2>&amp;1 | tail -30` returned success while its
    /// text said "Build FAILED".</para>
    /// </summary>
    private static bool BuildFailed(string result)
    {
        if (result.StartsWith("error", StringComparison.OrdinalIgnoreCase)) return true;

        ReadOnlySpan<string> markers =
        [
            "Build FAILED", "error CS", "error MSB",
            "Failed!", "FAILED", "Test Run Failed",
            "error[E", "error: could not compile",
            "npm ERR!", "Compilation failed", "SyntaxError", "cannot find symbol",
        ];
        foreach (var m in markers)
            if (result.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// The nudge for a goal whose edits do not build. Carries the build OUTPUT, because the model
    /// has already seen it once and moved on — repeating the fact without the detail would earn the
    /// same shrug.
    /// </summary>
    private static string BrokenBuildChallenge(string buildResult)
    {
        var detail = buildResult.Length > 1500 ? buildResult[..1500] + "…" : buildResult;
        return "The build is broken. Your changes were written, but the last build or test run "
             + "failed and you stopped without fixing it — a change that does not compile is not a "
             + "finished change. Fix it now, or revert your edits and say plainly why it cannot be "
             + "done.\n\nThe failing output was:\n" + detail;
    }

    /// <summary>One argument of a tool call as a string, or null when absent or not a string.</summary>
    private static string? TryGetArgument(ToolCall call, string name)
    {
        try
        {
            return call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object
                && call.Arguments.TryGetProperty(name, out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.String
                    ? v.GetString()
                    : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The plugin a tool dispatches to, for the transcript row's author label only.</summary>
    private static string ToolPluginType(string toolName) => toolName switch
    {
        "run_shell" => "shell",
        "http_request" => "http",
        _ => "file",
    };

    private static string DescribeCall(ToolCall call)
    {
        var args = call.Arguments.ToString();
        var detail = args.Length > 60 ? args[..60] + "…" : args;
        return $"{call.Name} {detail}";
    }

    /// <summary>
    /// Whether the user asked for a CHANGE, as opposed to an explanation. Deliberately conservative:
    /// it decides whether "wrote nothing" is a failure, and failing a question that was only ever a
    /// question would be worse than missing one edit.
    /// </summary>
    private static bool AsksForAChange(IReadOnlyList<ChatMessage> conversation)
    {
        var last = conversation.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        ReadOnlySpan<string> verbs =
        [
            "edit ", "modify ", "change ", "add ", "insert ", "replace ", "rewrite ", "fix ",
            "apply ", "update ", "remove ", "delete ", "write ", "create ", "implement ",
            "refactor ", "rename ",
        ];
        foreach (var v in verbs)
            if (last.Contains(v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? TryGetWorkingDirectory()
    {
        try { return Directory.GetCurrentDirectory(); }
        catch (Exception) { return null; }
    }
}
