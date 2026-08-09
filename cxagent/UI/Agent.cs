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
/// the DAG's parallelism is gone.</para>
/// </summary>
public sealed class Agent
{
    private readonly ILlmProvider _provider;
    private readonly PluginRegistry _plugins;
    private readonly TokenLedger _ledger;
    private readonly IChatSink _sink;
    private readonly IJobPanel _jobs;
    private readonly LogFileManager? _logs;
    private readonly int _maxTurns;

    /// <summary>
    /// This agent's identity, for its whole life. Keys its log directory and its job rows.
    ///
    /// <para>ONE ID, NOT ONE PER PROMPT. A fresh id was minted on every user message, so one linear
    /// session's diagnostics fragmented across directories with turn numbering restarting at 000 in
    /// each — and the session id on screen churned every time the user typed.</para>
    /// </summary>
    public string Id { get; } = Helpers.UlidGenerator.NewId();

    /// <summary>
    /// The last build and test verdicts, and the turn counter — session state, NOT per-prompt state.
    ///
    /// <para>These were locals in the turn loop, so they reset on every user message. A broken build
    /// is not forgotten because the user typed again: the tree is still broken, and the gate that
    /// catches it has to see the verdict that outlived the prompt. <c>_turn</c> is monotonic for the
    /// same reason the id is stable — log turn numbers that restart at 000 on each message make one
    /// session's diagnostics unreadable.</para>
    /// </summary>
    private string? _lastBuild;
    private string? _lastTest;
    private int _turn;

    /// <summary>
    /// This agent's conversation, for its whole life — the thing that makes it self-contained.
    ///
    /// <para>A field rather than a local inside <see cref="RunCoreAsync"/> because a context that
    /// exists only for the duration of one method cannot be owned by anything: not by a readout that
    /// wants to report real occupancy, not by a <c>/compress</c> that wants a single meaningful
    /// target, and not by an agent that is supposed to carry what it learned into its next task. A
    /// sub-agent gets its own <see cref="Agent"/> and therefore its own context, which is
    /// precisely what the fan-out design assumes it already had.</para>
    /// </summary>
    private readonly AgentContext _context;

    /// <summary>This agent's context — its messages, its occupancy, its window.</summary>
    public AgentContext Context => _context;

    /// <summary>Raised when a turn finishes, with its tool-call count. A callback rather than a
    /// GoalRunner reference: the loop needs to ANNOUNCE a turn boundary, not to know what listens.</summary>
    public Action<int>? TurnCompleted { get; set; }

    /// <summary>
    /// Raised after every turn with what the provider reported it RECEIVED — the live context size.
    ///
    /// <para>Separate from <see cref="TurnCompleted"/>, which carries only a tool-call count, and that
    /// gap was a real defect: in single-agent mode nothing else observes usage, so the status bar had
    /// no source for occupancy and fell back to the cumulative ledger total — a sum that outgrows any
    /// window and never falls, least of all after the compression this same number triggers.</para>
    /// </summary>
    public Action<int>? ContextUsed { get; set; }

    /// <summary>Raised when this loop's own per-turn compression actually shrank the conversation, so
    /// the readout can stop presenting its last measurement as current.</summary>
    public Action<int, int>? ContextCompressed { get; set; }

    /// <summary>Raised with a SCALED occupancy figure after compaction — arithmetic, not a
    /// measurement, so the readout marks it approximate until a real reading arrives.</summary>
    public Action<int>? ContextEstimated { get; set; }


    /// <summary>
    /// Input tokens past which the loop compresses its own context, or null to never compress.
    ///
    /// <para>THE BOUND THAT REPLACES THE TURN CAP. Single-agent has no turn ceiling by design — a
    /// number of turns has nothing to do with the task — and the comment at the construction site
    /// says the context window ends a session that cannot continue. It did not: GoalRunner's
    /// auto-compression sits in a `finally` around the whole GOAL, and a single-agent goal is ONE
    /// RunAsync that loops internally, so the check fired after the run that blew past it. Measured
    /// live at 1.16M input tokens against a 40,000 threshold, never once compressing.</para>
    /// </summary>
    private readonly int? _compressAbove;

    /// <param name="context">
    /// The agent's context. Optional so existing callers and tests keep working — omitting it gives
    /// this agent a fresh one of its own, which is the right default: an agent that is not handed a
    /// context still HAS one, rather than borrowing the caller's list.
    /// </param>
    public Agent(ILlmProvider provider, PluginRegistry plugins, TokenLedger ledger,
        IChatSink sink, IJobPanel jobs, LogFileManager? logs, int maxTurns, int? compressAbove = null,
        AgentContext? context = null)
    {
        _provider = provider;
        _plugins = plugins;
        _ledger = ledger;
        _sink = sink;
        _jobs = jobs;
        _logs = logs;
        _maxTurns = maxTurns;
        _compressAbove = compressAbove;
        _context = context ?? new AgentContext();
    }

    /// <summary>Every tool, always. Roles used to slice this per worker name; that mechanism is gone
    /// and safety lives in the permission gate, not in withholding capability.</summary>
    private static readonly IReadOnlyList<WorkerTool> AllTools = Enum.GetValues<WorkerTool>();

    /// <summary>
    /// One exchange on the linear path: prompt → tools → answer.
    /// </summary>
    /// <remarks>
    /// TAKES A PROMPT, RETURNS AN ANSWER. It used to take the caller's transcript list and mutate it,
    /// which coupled the agent's context to the UI's record of the conversation. The transcript is the
    /// UI's; <see cref="Context"/> is what the model sees. The caller appends both.
    ///
    /// <para>The <c>ToolCallId</c> hazard that justified rebuilding the context per prompt — a tool
    /// result outliving the call it belongs to, which providers reject — is handled where it belongs:
    /// the compressor snaps its split so a kept result always keeps its call.</para>
    ///
    /// <para>NO COMPRESSION CHECK AROUND THIS CALL. One used to run in a <c>finally</c> here, on the
    /// reasoning that a single-turn exchange has no "next turn" for the in-loop check to catch. It was
    /// a task-boundary trigger in a mode that no longer has task boundaries, it ran on
    /// <see cref="CancellationToken.None"/> so a cancelled session still paid for it, and the pre-send
    /// check at the top of the turn loop already guarantees nothing over the threshold is ever sent.</para>
    /// </remarks>
    public async Task<string> SendAsync(string prompt, CancellationToken ct)
    {
        // THE AGENT'S OWN CONTEXT, CARRIED ACROSS GOALS. This used to be
        // `new List<ChatMessage>(conversation)` — a fresh working list built from the session history
        // at the start of every goal and dropped at the end, so goal N's tool calls, file reads and
        // reasoning were gone before goal N+1 began (measured on a real run: 33 turns discarded, a
        // session falling from 58,000 tokens to ~5,000 the moment the goal ended). "Read X and explain
        // it" followed by "now change it" re-read X, because nothing of the first goal remained.
        //
        // Nobody else works that way: Claude Code, Codex, opencode, gemini-cli, Cline and goose all
        // keep ONE growing list across prompts and compact on TOKEN pressure rather than at a task
        // boundary. The rebuild also guaranteed a prompt-cache miss — those agents append to a stable
        // prefix precisely so cached reads keep hitting, and discarding cached context saves far less
        // than it costs to rebuild.
        var messages = _context.Messages;

        // The user's prompt joins the agent's context. The caller puts its own copy on the session
        // transcript; this is the one the MODEL sees. A plain append either way — the old branch on
        // `messages.Count > 0` existed only because an empty context was seeded from the caller's
        // list, and there is no caller list any more.
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Timestamp = DateTimeOffset.UtcNow,
        });


        // WHERE IT IS. A fresh context has never seen a shell prompt, and measured across one
        // session, ten of twenty shell calls were `find`/`ls` hunting for paths that do not exist on
        // this machine — /Users/<someone>/…, /home/user, bare /.
        //
        // ONCE PER AGENT, not once per goal: the context now persists, so re-inserting this on every
        // goal would stack a duplicate preamble at the front of a conversation that already has one.
        var cwd = TryGetWorkingDirectory();
        if (cwd is not null && messages.All(m => m.Role != "system"))
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

        // The last build and test verdicts are FIELDS (_lastBuild/_lastTest), not locals: see their
        // declaration. A broken build outlives the prompt that broke it.

        // Identical (call, arguments, result) triples seen this request, for stuck detection below.
        // Per-request deliberately: a new user message is a genuine perturbation, and carrying the
        // counts across it would nudge about repeats the user has already redirected.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        // Times the server claimed "tool_use" while no call was parsed. Bounded so a server that
        // reports it on EVERY turn cannot spin the loop.
        var toolUseMismatches = 0;

        // TWO COUNTERS, and they answer different questions. `turn` bounds THIS request against
        // _maxTurns; `_turn` numbers log files across the agent's whole life. Folding them into one
        // would silently tighten the cap on every prompt — the second message in a session would
        // start with the first message's turns already counted against it.
        //
        // _turn IS ADVANCED IN THE BODY, not here. In the increment clause it only ran when the loop
        // CONTINUED, and the commonest turn of all — a prose answer — returns from inside the body
        // instead. So a session of one-turn exchanges logged every prompt as context-000, each
        // silently overwriting the last: the exact log fragmentation this counter exists to prevent,
        // reintroduced by the one path that never reaches a `continue`.
        for (var turn = 0; ; turn++)
        {
            ct.ThrowIfCancellationRequested();

            // COMPRESS ON PRESSURE, BEFORE SENDING — not after the response, which is where this used
            // to sit and where it could not work. A turn's TOOL RESULTS are appended after the
            // response is handled, so a check placed there tests the size of the conversation as it
            // was BEFORE this turn's file reads landed: it fired on a reading that predated the growth
            // it was meant to relieve, and the goal then ended with the grown context never
            // re-measured. Measured live: compaction reported −32% while the token figure moved 20,
            // because it had removed exactly the content that arrived after the last measurement.
            //
            // Here the previous turn's results are in, so the check describes what is about to be
            // sent — but only because it uses ProjectedUsed rather than the raw reading: the
            // measurement is always one turn behind the growth, so testing it directly let a context
            // sent at 98,630 characters pass a threshold on a reading taken at 66,394.
            if (_context.ProjectedUsed is { } occupancy)
                await MaybeCompressAsync(Id, occupancy, ct);

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
                _sink.ShowError($"stopped after {_maxTurns} turns without finishing.");

                // The salvaged summary IS the answer on this path — the caller puts it on the
                // transcript, exactly as it does for an ordinary reply.
                return summary;
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

            // BEFORE the call, so what is recorded is what was actually sent — including on a turn
            // that then fails, which is the one you most want to look at afterwards. The token count
            // carried is the PREVIOUS turn's measurement, since this turn's has not happened yet.
            LogContext(Id, _turn, messages, _context.Used);

            // THE SIZE THE PROVIDER IS ABOUT TO SEE. Captured here rather than after the response,
            // because by then this turn's reply and tool results have been appended and the figure no
            // longer describes what the reading covers.
            var sentChars = _context.TotalChars();

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

            // RECORD IT ON THE CONTEXT, which needs both the reading and the size it was taken at to
            // estimate honestly after a compaction. Published BEFORE the compression check below, so
            // the reading that TRIGGERS a compression is the one the user sees; the row that follows
            // then explains the drop.
            _context.RecordUsage(response.Usage.InputTokens, sentChars);
            if (response.Usage.InputTokens > 0)
                ContextUsed?.Invoke(response.Usage.InputTokens);


            // LOG THE RAW RESPONSE. Only tool RESULTS were ever written, so the model's own output —
            // the prose, the reasoning, the markdown — existed nowhere once the screen scrolled. A
            // rendering bug reported from a screenshot was undiagnosable: the input that produced it
            // could not be recovered, and every hypothesis about it stayed a guess.
            //
            // Raw, before StripReasoning, because the reasoning block is part of what arrived and a
            // fault in the stripping itself would be invisible in stripped output.
            LogTurn(Id, _turn, response);

            // THIS turn's number is now spent — both LogContext above and LogTurn just now used it,
            // so they pair up as context-NNN/turn-NNN. Advanced here rather than in the loop header
            // so that a turn which RETURNS still counts: see the note there.
            _turn++;

            TurnCompleted?.Invoke(response.ToolCalls.Count);

            var text = ModelOutput.StripReasoning(response.Text);

            // Nothing more will be appended to this turn. Closing it stops the spinner; the text (if
            // any) was streamed in as it arrived.
            _sink.EndAssistantTurn(turnId);

            // KEEP GOING IF THE SERVER SAID "tool_use" BUT WE PARSED NO CALLS. The two disagree only
            // when something went wrong in between — a truncated stream, a malformed arguments blob
            // the accumulator dropped — and ending the goal there discards a turn the model believed
            // it was mid-way through.
            //
            // The mirror case is the one opencode documents: "Some providers return 'stop' even when
            // the assistant message contains tool calls." Both it and crush therefore AND the stop
            // reason with a real scan for tool calls rather than trusting either alone, and a local
            // llama.cpp or vLLM endpoint is exactly the kind that gets this wrong. Trusting the
            // PARSED CALLS as the primary signal covers that half; this covers the other.
            if (response.ToolCalls.Count == 0
                && response.StopReason == "tool_use"
                && toolUseMismatches < MaxToolUseMismatches)
            {
                toolUseMismatches++;
                messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "Your last response was cut off before its tool call arrived. Re-issue "
                            + "the call you intended, or say what you want to do next.",
                    Timestamp = DateTimeOffset.UtcNow,
                });
                continue;
            }

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

                // Two ways a change request can finish badly, and they need different words: nothing
                // was written at all, or something was written that does not build.
                var brokenBuild = wrote && _lastBuild is not null && BuildFailed(_lastBuild);
                var brokenTest = wrote && _lastTest is not null && BuildFailed(_lastTest);
                var broken = brokenBuild || brokenTest;

                // THE PROMPT, not a scan back through the conversation. It used to take the last user
                // message off the caller's transcript, which is the same thing by construction — this
                // request's prompt — but only as long as the transcript's last user entry was the one
                // being answered. Judging the argument says exactly what is meant.
                var unfinished = !wrote && AsksForAChange(prompt);

                if ((unfinished || broken) && !refused && challenges < MaxChallenges)
                {
                    challenges++;
                    messages.Add(new ChatMessage
                    {
                        Role = "user",
                        // The FAILING one's output, and the build first when both are red: a test
                        // failure reported against a tree that does not compile is noise.
                        Content = broken
                            ? BrokenBuildChallenge(brokenBuild ? _lastBuild! : _lastTest!)
                            : ChallengeText(challenges),
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    continue;
                }

                // NOT A SILENT SUCCESS when the request wanted a change and none was made. Reporting
                // success over an unchanged working tree is the same lie this mode was built to stop,
                // one level up: the run says done, the disk says otherwise, and the user finds out
                // later. The error is the signal — the caller discarded the status enum this used to
                // return, so the sink is what actually reaches anyone.
                if (unfinished)
                    _sink.ShowError(
                        "you asked for a change, but nothing was written. Investigation ran to a "
                        + "stop without reaching an edit.");

                // A BROKEN BUILD IS A FAILED REQUEST. Measured live: a correct diagnosis, a patch that
                // did not compile, "Build FAILED" in the transcript, and a confident success summary
                // in the same turn. Edits were made, so the no-write gate above saw nothing wrong —
                // this is the one that has to catch it.
                else if (broken)
                    _sink.ShowError(
                        "changes were written but the build did not succeed. The last build or test "
                        + "run reported a failure and it was not resolved.");

                // The answer, either way. It is already ON SCREEN — it streamed into the turn opened
                // above — so this is for the caller's transcript, and it is returned rather than
                // pushed onto a list the agent was handed.
                return text;
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
                var result = await InvokeAndShowAsync(Id, call, ct);
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
                var signature = call.Name + "\0" + call.Arguments.ToString() + "\0" + result;
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

                // A build or test run REPLACES the previous verdict of ITS OWN KIND rather than
                // accumulating: what matters at the end is whether the tree compiles NOW and whether
                // the tests pass NOW, not whether either ever did. A model that breaks the build,
                // fixes it, and stops has finished the job.
                //
                // Two slots, not one. A build and a test answer different questions, and folding
                // them together lets the answer to one erase the answer to the other — see lastTest.
                if (call.Name == "run_shell" && LooksLikeBuildOrTest(call))
                {
                    if (LooksLikeTest(call)) _lastTest = result;
                    else _lastBuild = result;
                }

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
            return ModelOutput.StripReasoning(response.Text);
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
    /// Writes one turn's raw model output to the goal's log directory, fire-and-forget.
    ///
    /// <para>Uses the same store as tool results, under a per-turn id, so a session reads in order:
    /// what the model said, then what its calls returned. The tool-call names and arguments are
    /// recorded alongside the prose because a turn is often ONLY calls, and a log that showed
    /// nothing for those turns would look like the model had gone silent.</para>
    ///
    /// <para>Never throws and never awaits: logging is diagnostics, and a goal must not fail — or
    /// stall — because a disk did.</para>
    /// </summary>
    /// <summary>
    /// Records WHAT WAS SENT this turn — the context, one line per message.
    ///
    /// <para>The response was logged from the start; the input never was, and that is the half you
    /// need to answer the questions that actually come up: why did the model not know something it
    /// was told, what is occupying the window, did compaction drop the wrong thing. A tool result
    /// that has been pruned shows as its tombstone here, so a gap in the model's knowledge can be
    /// traced to the turn that created it.</para>
    ///
    /// <para>Sizes and roles rather than full content: the whole point is to see the SHAPE of a
    /// context that may be hundreds of thousands of characters, and a log that reproduces all of it
    /// on every turn is one nobody opens twice. The first line of each message is enough to
    /// recognise it.</para>
    /// </summary>
    private void LogContext(string agentId, int turn, IReadOnlyList<ChatMessage> messages, int? inputTokens)
    {
        if (_logs is null) return;

        try
        {
            var sb = new StringBuilder();
            var chars = 0;
            foreach (var m in messages) chars += m.Content?.Length ?? 0;

            sb.AppendLine($"=== turn {turn:D3} · {messages.Count} messages · {chars:N0} chars"
                        + (inputTokens is { } t ? $" · {t:N0} input tokens" : "") + " ===");

            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                var role = m.ToolCallId is not null ? "tool" : m.Role;
                var body = (m.Content ?? "").ReplaceLineEndings(" ");
                var head = body.Length <= 120 ? body : body[..120] + "…";
                var calls = m.ToolCalls is { Count: > 0 }
                    ? " [calls: " + string.Join(", ", m.ToolCalls.Select(c => c.Name)) + "]"
                    : "";
                sb.AppendLine($"[{i:D3}] {role,-9} {(m.Content?.Length ?? 0),8:N0}ch{calls}  {head}");
            }

            _ = _logs.AppendAsync(agentId, $"context-{turn:D3}", "log", sb.ToString());
        }
        catch (Exception)
        {
            // Diagnostics must never take down the thing they are diagnosing.
        }
    }

    private void LogTurn(string agentId, int turn, LlmResponse response)
    {
        if (_logs is null) return;

        try
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(response.Text)) sb.AppendLine(response.Text);

            foreach (var call in response.ToolCalls)
                sb.AppendLine($"→ {call.Name} {call.Arguments}");

            if (sb.Length == 0) return;

            // "log", not "response": PathFor VALIDATES the stream against log/stdout/stderr and
            // throws on anything else. An invented name would have thrown on every turn, been
            // swallowed by the catch below, and logged nothing at all — a diagnostic that silently
            // does not work is worse than none, because it is trusted.
            _ = _logs.AppendAsync(agentId, $"turn-{turn:D3}", "log", sb.ToString());
        }
        catch (Exception)
        {
            // Diagnostics must never take down the thing they are diagnosing.
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
    private async Task<string> InvokeAndShowAsync(string agentId, ToolCall call, CancellationToken ct)
    {
        var jobId = Helpers.UlidGenerator.NewId();
        var job = new Job
        {
            Id = jobId,
            PlanLocalId = call.Name,
            AgentId = agentId,
            PluginType = ToolPluginType(call.Name),
            DisplayName = DescribeCall(call),
            State = JobState.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _jobs.SetJobs(new[] { job });

        var started = DateTimeOffset.UtcNow;
        var ctx = new JobContext(agentId, jobId, new Dictionary<string, JobResult>(), _logs);
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
        var stop = "";

        // How much of the (reasoning-stripped) text has already been shown. Deltas arrive raw, and a
        // reasoning block can span many of them, so what is SAFE to display is recomputed from the
        // accumulated text after each chunk rather than appended blindly — streaming the raw delta
        // would put the model's <think> block on screen, which is exactly what StripReasoning exists
        // to prevent.
        var shown = 0;

        // Same trick for reasoning: a reasoning block spans many deltas, so only the part not yet
        // written is appended after each chunk.
        var shownReasoning = 0;

        await foreach (var chunk in _provider.ChatStreamAsync(messages, tools, ct))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                text.Append(chunk.TextDelta);

                var accumulated = text.ToString();

                var visible = ModelOutput.StripReasoning(accumulated);
                if (visible.Length > shown)
                {
                    _sink.AppendAssistant(turnId, visible[shown..]);
                    shown = visible.Length;
                }

                // REASONING GOES IN THE BODY, dimmed — not into the header.
                //
                // It WAS a one-line header that rewrote itself on every new line of thought, and
                // that was wrong twice over. A single line that overwrites itself discards the
                // reasoning as fast as it arrives, so the thing it is meant to make visible can
                // never actually be read; and because nothing clears the header when the turn ends,
                // the last line of thinking stayed welded to the finished message as its title.
                //
                // The body can hold all of it, in order, where it scrolls with everything else.
                //
                // AMBER, not [dim]. Dim asks the terminal to render the SAME colour more faintly,
                // which is a request many terminals ignore and none render identically — and against
                // an already-dark background the ones that honour it produce grey mush. A colour of
                // its own says "this is a different KIND of text" rather than "this text matters
                // less", which is what reasoning actually is.
                //
                // The cost is the spinner: ChatTranscriptControl clears a message's thinking flag as
                // soon as body content arrives. That is the right trade — the reasoning text itself
                // is now the evidence the model is alive, and it is far better evidence than a
                // spinner, because it says WHAT the model is doing.
                var reasoning = ModelOutput.ExtractReasoning(accumulated);
                if (reasoning.Length > shownReasoning)
                {
                    _sink.AppendAssistant(turnId,
                        $"[{ColorScheme.ThinkingMarkup}]{Escape(reasoning[shownReasoning..])}[/]");
                    shownReasoning = reasoning.Length;
                }
            }

            if (chunk.ToolCallDelta is { } tc) calls.Add(tc);
            if (chunk.Usage is { } u) usage = u;
            if (chunk.StopReason is { Length: > 0 } sr) stop = sr;
        }

        return new LlmResponse
        {
            Text = text.ToString(), ToolCalls = calls, Usage = usage, StopReason = stop,
        };
    }


    /// <summary>
    /// Summarises the older half of <paramref name="messages"/> when the last turn's input crossed
    /// <see cref="_compressAbove"/>.
    ///
    /// <para>THROUGH THE MODEL, not by eviction. Dropping tool results and leaving receipts was the
    /// obvious cheap fix and it is the wrong one: a file read is not dead weight once consumed — what
    /// the model CONCLUDED from it is the value, and that lives nowhere else. Only the model can tell
    /// "this defines the interface I am changing" from "this was irrelevant", and a size-based rule
    /// loses both identically. Every agent in this space compacts by asking the model to write a
    /// handoff, and SessionCompressor already does exactly that.</para>
    ///
    /// <para>Never throws: compression failing must not end a goal that is otherwise working.
    /// SessionCompressor falls back to truncation on a provider error, and its result says which
    /// happened so the transcript can be honest about it.</para>
    /// </summary>
    private async Task MaybeCompressAsync(string agentId, int inputTokens, CancellationToken ct)
    {
        if (_compressAbove is not { } threshold || inputTokens <= threshold) return;

        // The row itself lives in CompressionRun, which every compressing route now shares — this one,
        // GoalRunner's between-goals check, and the /compress command. The threshold test stays here
        // because only this caller measures per-turn pressure.
        await CompressionRun.RunAsync(_context, _provider, _jobs, agentId,
            $"compress context · {inputTokens:N0} tokens over {threshold:N0}",
            _ledger.Record, ct, compressed: (b, a) =>
            {
                ContextCompressed?.Invoke(b, a);
                // The context re-estimated its own occupancy while compacting; publish it so the
                // readout shows where that leaves us rather than the pre-compaction figure.
                if (_context.Used is { } estimated) ContextEstimated?.Invoke(estimated);
            });
    }

    private static bool IsWrite(string toolName) =>
        toolName is "write_file" or "replace_in_file";

    /// <summary>How many times a no-write finish is challenged before the goal is failed.</summary>
    private const int MaxChallenges = 3;

    /// <summary>Identical repeats of one (call, arguments, result) before the model is told; twice
    /// that many before the goal is failed. Three is high enough that a legitimate re-read after
    /// changing something is never mistaken for a loop.</summary>
    private const int StuckRepeats = 3;

    /// <summary>Retries for a "tool_use" turn that carried no parseable call, before the response is
    /// taken at face value. Two, because a genuine truncation is transient and a server that always
    /// misreports would otherwise never let the goal end.</summary>
    private const int MaxToolUseMismatches = 2;

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
    /// <summary>
    /// Whether a shell call is running TESTS specifically, as opposed to compiling.
    ///
    /// <para>Both are gates on a finished goal, but they must be remembered separately: a rebuild
    /// after a failing test run would otherwise overwrite the failure with a success and let the
    /// goal finish red. Deliberately a subset of <see cref="LooksLikeBuildOrTest"/> — anything that
    /// is not recognisably a test run is treated as a build, so a new verb defaults to the stricter
    /// reading rather than being silently ignored.</para>
    /// </summary>
    private static bool LooksLikeTest(ToolCall call)
    {
        var cmd = TryGetArgument(call, "command");
        if (string.IsNullOrEmpty(cmd)) return false;

        ReadOnlySpan<string> verbs =
        [
            "dotnet test", "cargo test", "go test",
            "npm test", "yarn test", "pnpm test", "pytest", "vitest", "jest",
        ];
        foreach (var v in verbs)
            if (cmd.Contains(v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

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

    /// <summary>The last non-blank line of a reasoning stream, capped for a one-line header.</summary>
    private static string LastLine(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            return line.Length > 110 ? line[..110] + "…" : line;
        }
        return "";
    }

    /// <summary>
    /// Escapes markup so reasoning text cannot be interpreted as tags.
    ///
    /// <para>A model reasoning ABOUT markup writes "[dim]" and "[/]" as ordinary words, and an
    /// unescaped one would open a style scope that never closes — corrupting every header after it.
    /// </para>
    /// </summary>
    private static string Escape(string text) => text.Replace("[", "[[");

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
    private static bool AsksForAChange(string prompt)
    {
        var last = prompt ?? "";
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
