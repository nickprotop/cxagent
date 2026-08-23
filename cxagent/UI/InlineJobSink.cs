using System.Collections.Concurrent;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;

using CxAgent.Core.Sessions;
// Ours, not SharpConsoleUI's — both exist and this file sees both namespaces.
using ChatMessageId = CxAgent.Core.Sessions.ChatMessageId;
using CxAgent.Core.Helpers;
using CxAgent.Core.Agents;
using CxAgent.Core.Commands;

namespace CxAgent.UI;

/// <summary>
/// An <see cref="IToolObserver"/> that writes jobs INTO the conversation instead of a side panel —
/// one column, jobs interleaved with the turns that caused them.
///
/// <para>Each job becomes one transcript message whose STATUS row is rewritten in place as the job
/// moves Pending → Running → Succeeded/Failed. <c>ChatTranscriptControl.SetStatus(id, text, severity)</c>
/// is keyed by message id, so a job's line updates where it already sits rather than appending a new
/// line per transition — which would bury the conversation under status spam on a seven-job goal.</para>
///
/// <para>This exists at all because <see cref="IToolObserver"/> was already the seam between AgentHost
/// and the UI: the engine calls ToolsChanged/ToolUpdated and never touches a control. Swapping the layout is
/// therefore a UI-only change — no engine edits, and the copilot draft gate keeps working unchanged
/// because it too speaks through this interface.</para>
/// </summary>
public sealed class InlineJobSink : IToolObserver
{
    private readonly ConsoleWindowSystem _system;
    private readonly ChatTranscriptControl _chat;

    /// <summary>
    /// Job id → the transcript message showing it. ConcurrentDictionary because AgentHost raises
    /// JobTransitioned from whatever thread the finishing job's continuation resumed on — the same
    /// reason OrchestratorLoop's finished-queue is concurrent.
    /// </summary>
    private readonly ConcurrentDictionary<string, SharpConsoleUI.Controls.ChatMessageId> _lines = new();


    

    public InlineJobSink(ConsoleWindowSystem system, ChatTranscriptControl chat)
    {
        _system = system;
        _chat = chat;
    }

    /// <summary>
    /// The plan compiled. One message per job, in plan order, so the user reads the shape of the work
    /// before any of it runs — and in copilot mode, before deciding whether to let it.
    /// </summary>
    public void ToolsChanged(IReadOnlyList<Job> jobs) =>
        _system.EnqueueOnUIThread(() => ToolsChangedNow(jobs));

    /// <summary>
    /// The add path's work, split from its marshalling for the same reason as
    /// <see cref="RefreshRunningHeadersNow"/>: a test needs rows on screen before it can watch the
    /// tick rewrite them, and the enqueue above hands the work to a loop that is not running in a
    /// unit test. Must be on the UI thread; <see cref="ToolsChanged"/> is what guarantees it.
    /// </summary>
    public void ToolsChangedNow(IReadOnlyList<Job> jobs)
    {
        {
            // A re-plan (or a mid-goal addition) calls this again with the full set. Only add lines
            // for jobs not already on screen; re-adding every job each time would duplicate the
            // whole plan in the transcript.
            foreach (var job in jobs)
            {
                _known[job.Id] = job;
                if (_lines.ContainsKey(job.Id)) continue;
                if (!ShouldShow(job)) continue;

                var id = _chat.AddMessage(ChatRole.Tool, Title(job), author: AuthorFor(job));
                _lines[job.Id] = id;

                var compactRow = IsCompactRow(job) || !IsTerminal(job.State);

            // A COMPACT step carries its state in the HEADER and has NO status row: "Tool  Read
            // Base64Decoder.cs  ·  done · 0.0s" says everything the separate row said, on a line
            // that already existed. That is the third line removed — header + result, not header +
            // result + status.
            //
            // While running the header carries an inline [spinner], so the row does not change
            // SHAPE when it finishes; it just stops spinning.
            if (compactRow)
            {
                _chat.SetHeader(id, CompactHeader(job));
                _chat.ClearStatus(id);
                // CLEAR THE BODY. The message was CREATED with Title(job) as its body, and a job
                // that has not run has no output to replace it — so a queued row printed its own
                // name twice, once in the header and once below. Seen live on a fan-out: four
                // pending rows each repeating themselves.
                _chat.UpdateMessage(id, string.Empty);
                // EXPAND it too. The Tool role is StartCollapsed, so a newly-planned row opens with
                // an "expand…" affordance over an empty body — an invitation to reveal nothing.
                // ToolUpdated already does this; ToolsChanged did not, so every row showed it until its
                // first transition.
                //
                // EXCEPT A WORKER. That reasoning holds only while the body IS empty, and a spawn
                // fills its body as it runs (the child's recent calls) and again when it finishes
                // (the child's report). Auto-expanding here opens a block that then GROWS under the
                // reader for minutes, pushing the conversation away — and the user never asked for
                // it. The affordance is the point: `expand…` says there is something, and they
                // choose.
                if (job.JobType != "llm_agent")
                    _chat.SetExpanded(id, true);
            }
            else
            {
                // A NON-COMPACT row gets the header too. Only the compact branch set it, so a
                // streaming worker — which is switched OUT of compact mode on its first delta — kept
                // the spinner it was given while pending, FOREVER. Seen live on a fan-out: five
                // tools showing ✔ beside workers still spinning after they had finished and
                // collapsed.
                _chat.SetHeader(id, CompactHeader(job));
                // NO STATUS ROW ON A WORKER. CompactHeader already ends with "· done · 73.6s", and
                // SetStatus prints those same two facts again UNDER the body — seen live on a
                // finished spawn: header `done · 73.6s`, then the report, then `done · 73.6s` once
                // more. A row that says one thing twice teaches the reader to skip both.
                //
                // Tools keep theirs: a tool's status row is where a failure's reason surfaces, and
                // its header is shorter. This is about worker rows, which are the verbose ones.
                // A PLAN ROW IS THE SAME CASE. Its header already ends `· done · 0.0s`, and the
                // status row repeated it under the list with a full-width rule between — three
                // lines of chrome around six lines of content, which is what pushed the list itself
                // behind an `expand…`.
                if (IsTheAnswer(job)) _chat.ClearStatus(id);
                else _chat.SetStatus(id, StatusText(job), SeverityFor(job.State));
            }

            // COMPACT the chrome for a row with nothing to read. Measured on a real drive: one tool
            // step rendered as header + body + a full-width separator rule + status + a blank line —
            // five lines to say "ran a command, got 20". Six such steps cost ~26 lines of transcript
            // for six lines of substance, which is what pushed the conversation off screen.
            //
            // The rule and the trailing blank are framework-forced on every SetStatus
            // (ApplyFooterSeparator/ApplyFooterSpacer re-derive them), so this needs the per-message
            // opt-out rather than setting properties on the rows.
            // A RUNNING or PENDING job has no output yet, so it is compact BY DEFINITION — there is
            // nothing to expand into. Gate this on IsTerminal and a live tool renders the full block
            // with an "expand…" affordance revealing an empty body, which is exactly the noise the
            // compact footer exists to remove — and it sits on screen for the whole time the tool
            // runs.
            _chat.SetCompactFooter(id, IsCompactRow(job) || !IsTerminal(job.State));
            }
        }
    }

    public void ToolUpdated(Job job) =>
        _system.EnqueueOnUIThread(() =>
        {
            _known[job.Id] = job;

            // A hidden job gains a row only if it later FAILS — ShouldShow flips then, and the
            // adopt-on-first-update path below creates the line it never had.
            if (!ShouldShow(job)) return;

            // A job the orchestrator added mid-goal can transition before any ToolsChanged mentions it —
            // adopt it rather than dropping the update on the floor.
            if (!_lines.TryGetValue(job.Id, out var id))
            {
                id = _chat.AddMessage(ChatRole.Tool, Title(job), author: AuthorFor(job));
                _lines[job.Id] = id;
            }
            var compactRow = IsCompactRow(job) || !IsTerminal(job.State);

            // A COMPACT step carries its state in the HEADER and has NO status row: "Tool  Read
            // Base64Decoder.cs  ·  done · 0.0s" says everything the separate row said, on a line
            // that already existed. That is the third line removed.
            //
            // While running the header carries an inline [spinner], so the row does not change
            // SHAPE when it finishes; it just stops spinning.
            if (compactRow)
            {
                _chat.SetHeader(id, CompactHeader(job));
                _chat.ClearStatus(id);
            }
            else
            {
                // THE HEADER, on the non-compact path too. This is the twin of the branch in
                // ToolsChanged, and for a long time only that one set the header -- so a STREAMING
                // worker, which ToolOutputAppended switches out of compact mode on its first delta, kept
                // the pending spinner it was born with for the rest of the session. ToolsChanged is the
                // add path and rarely fires on completion; ToolUpdated is where a job actually
                // finishes, so the row that most needed the header was the one never getting it.
                // Seen live: a worker showing a spinning Braille frame beside its own finished
                // review body, with "Goal completed." printed below it.
                _chat.SetHeader(id, CompactHeader(job));
                // NO STATUS ROW ON A WORKER. CompactHeader already ends with "· done · 73.6s", and
                // SetStatus prints those same two facts again UNDER the body — seen live on a
                // finished spawn: header `done · 73.6s`, then the report, then `done · 73.6s` once
                // more. A row that says one thing twice teaches the reader to skip both.
                //
                // Tools keep theirs: a tool's status row is where a failure's reason surfaces, and
                // its header is shorter. This is about worker rows, which are the verbose ones.
                // A PLAN ROW IS THE SAME CASE. Its header already ends `· done · 0.0s`, and the
                // status row repeated it under the list with a full-width rule between — three
                // lines of chrome around six lines of content, which is what pushed the list itself
                // behind an `expand…`.
                if (IsTheAnswer(job)) _chat.ClearStatus(id);
                else _chat.SetStatus(id, StatusText(job), SeverityFor(job.State));
            }

            // COMPACT the chrome for a row with nothing to read. Measured on a real drive: one tool
            // step rendered as header + body + a full-width separator rule + status + a blank line —
            // five lines to say "ran a command, got 20". Six such steps cost ~26 lines of transcript
            // for six lines of substance, which is what pushed the conversation off screen.
            //
            // The rule and the trailing blank are framework-forced on every SetStatus
            // (ApplyFooterSeparator/ApplyFooterSpacer re-derive them), so this needs the per-message
            // opt-out rather than setting properties on the rows.
            // A RUNNING or PENDING job has no output yet, so it is compact BY DEFINITION — there is
            // nothing to expand into. Gate this on IsTerminal and a live tool renders the full block
            // with an "expand…" affordance revealing an empty body, which is exactly the noise the
            // compact footer exists to remove — and it sits on screen for the whole time the tool
            // runs.
            _chat.SetCompactFooter(id, compactRow);

            // EXPAND a compact row. SetCompactFooter removes the separator rule and the trailing
            // blank, but the "expand…" affordance is a DIFFERENT mechanism — the Tool role sets
            // StartCollapsed = true, so every row opens collapsed regardless of what its footer
            // does. A compact row has nothing behind that affordance (its whole content is the one
            // line), so leaving it collapsed offers to expand into a copy of the header, which is
            // exactly the noise being removed. Seen live: "▸ Tool │   expand… │ running…".
            // A WORKER IS NEVER AUTO-EXPANDED, running or finished. The rule above is right for a
            // TOOL — its compact row's whole content is the one line, so leaving it collapsed offers
            // to expand into a copy of the header. A sub-agent is the case that rule was not written
            // for: while running it has a live progress body (the child's recent calls), and when it
            // finishes it has the child's whole report. Opening either without being asked puts a
            // block on screen that grows under the reader and pushes the conversation away.
            //
            // The affordance is the point: `expand…` says there IS something, and the user decides.
            //
            // A PLAN IS THE EXCEPTION ON BOTH COUNTS. It is not compact — its body is the list, not
            // a copy of the header — and it must still open, because the list IS what the call
            // produced and hiding it behind `expand…` defeats the whole row. Unlike a worker's it
            // does not grow under the reader: it is a handful of short lines, written once, and it
            // is the answer rather than the working.
            if ((compactRow && job.JobType != "llm_agent") || job.JobType == "todo")
                _chat.SetExpanded(id, true);

            // THE BODY, not only the status row. Touch the status row alone and a job's message
            // stays whatever Title() produced when it first appeared — its NAME. The model's actual
            // output is written by every executor (LlmAgentJobPlugin's transcript, ShellJobExecutor's
            // stdout, both into Output["content"]) and read by nothing but IntrospectionTools, which
            // is how the ORCHESTRATOR reads results — leaving the user unable to see what their own
            // workers said.
            //
            // Terminal states only: a Running job has no output yet, and rewriting the body on every
            // transition would churn the transcript for no gain.
            // A MECHANICAL step (file/http/wait, and a shell that SUCCEEDED) gets no body at all: it
            // is one operation, milliseconds, with nothing to read. A goal that reads three files and
            // runs one worker should not render four visually equal blocks, three of them
            // bookkeeping — the transcript's weight belongs on work that took time and produced
            // something. Its name and status row still say what happened.
            //
            // Failure overrides type: a failed shell job needs its stderr and its buttons.
            // A job that went BACK to Running (a retry) must lose the previous attempt's body, or it
            // shows the OLD failure's stderr under a "running…" status — seen live: a row displaying
            // `/bin/sh: 1: Syntax error: "(" unexpected` while the corrected command was executing.
            // Stale output under a live status is worse than no output: it describes work that is no
            // longer happening.
            // Clear the body while it runs: the message was CREATED with Title(job) as its body, so
            // a running tool otherwise offers to expand into a copy of its own header. Also drops a
            // previous attempt's output when a job is RETRIED — stale stderr under a live "running…"
            // describes work that is no longer happening.
            // A RUNNING ROW SHOWS WHAT IT IS DOING, when it has anything to say. Blanking is still
            // right for a job with no progress body — the message was CREATED with Title(job) as its
            // body, so a running tool would otherwise offer to expand into a copy of its own header,
            // and a RETRIED job would show the previous attempt's stderr under a live "running…".
            //
            // But a sub-agent does have something: its child's recent tool calls. Blank it too and
            // expanding a running spawn reveals an empty block, which is the worst moment to show
            // nothing.
            //
            // AND WHEN THE CHILD HAS CALLED SOMETHING, that something is the better answer: the same
            // timetable the row will settle into, so a reader watching it grow is not handed a
            // different artefact at the finish line. ProgressBody is what a spawn shows until its
            // first call lands, and what every other running row shows always.
            if (!IsTerminal(job.State))
                _chat.UpdateMessage(id, RunningWorkerBody(job) ?? job.ProgressBody ?? string.Empty);

            // A COMPACT row folds its whole result into ONE line and drops the expand affordance:
            // "20" does not need a header, a body, a full-width rule, a status row and a blank line
            // to be read. The header carries the name, the ⎿ carries the result.
            if (IsTerminal(job.State) && IsCompactRow(job))
            {
                // THE WHOLE OUTPUT, in the body, COLLAPSED. Folding to the one-line summary and
                // throwing the body away makes "⎿ 254 lines, 8,990 chars" all you can ever see, with
                // the actual text only in the log file. The summary line stays as the collapsed
                // header and the full output lives one keypress behind it.
                //
                // Kept collapsed by default because a fan-out of tools each returning hundreds of
                // lines would bury the conversation; the point is that the text is THERE when
                // wanted, not that it is always on screen.
                var full = BodyFor(job);
                if (string.IsNullOrWhiteSpace(full))
                {
                    _chat.UpdateMessage(id, OneLineRow(job)!);
                    _chat.SetExpanded(id, true);
                }
                else
                {
                    _chat.UpdateMessage(id, $"{OneLineRow(job)}\n\n{full}");
                    _chat.SetExpanded(id, false);
                }
            }
            else if (IsTerminal(job.State))
            {
                // Empty string, not null-and-skip. UpdateMessage sets the BODY, but the message was
                // CREATED with Title(job) as its body — so a job with no output kept its own name
                // there, and the row rendered as "Explore repository structur…  expand…" whose
                // expansion revealed the same title again. Seen in a live screenshot: two of six jobs
                // were pure noise, offering to expand into nothing. Clearing it collapses the row to
                // its header, which is all such a job has to say.
                // THE ENVELOPE'S TAGS ARE STRIPPED FOR DISPLAY. <sub_agent id=… state=…> is addressed
                // to the PARENT'S MODEL — it exists so a capped run cannot be mistaken for a finished
                // one — and it is the first line of the body, so it becomes the collapsed row's
                // preview. Seen live: a completed spawn previewed as its own id and state, telling a
                // reader nothing the header had not already said in plainer words.
                //
                // What a person wants behind that expand is the CHILD'S REPORT. The state is not lost:
                // it is in the header (done / failed) and, for a capped or stuck run, in the note the
                // envelope carries above the text — which survives because only the tags are removed.
                //
                // A FINISHED WORKER IS THE EXCEPTION, and it gets its TIMETABLE instead of its prose.
                // The narration a worker composes is the wrong artefact for a run that is over: the
                // question at the finish line is what it DID, and the prose answers a different one
                // at unbounded length. WorkerBody renders the calls; every other row falls through
                // to the line below unchanged.
                //
                // THE PROSE IS NOT DELETED. It stays in Output["content"], which IntrospectionTools
                // reads — that is how the orchestrator consumes a worker's result — so a later
                // opt-in (a second expand level, or a `/worker <id>` command) can surface it with no
                // new plumbing.
                _chat.UpdateMessage(id, WorkerBody(job) ?? StripEnvelope(BodyFor(job)) ?? string.Empty);

                // A SUCCEEDED job collapses (the user's call): a five-job fan-out each returning
                // paragraphs would push the conversation off screen, and its outcome is readable
                // from the header alone.
                //
                // A FAILED job EXPANDS. Collapsing it hides the error behind an "expand…" on the one
                // message the user must act on — and the buttons sit right there, so the question
                // ("what went wrong?") and the answer to it must be visible together. Only on the
                // transition, never re-applied: a user who collapses a failed job has read it, and
                // re-expanding under them on the next update would fight them.
                // A WORKER stays EXPANDED; a tool collapses. The user asked for this, and streaming
                // is what makes it right: the worker's prose was already visible, growing, for the
                // whole time it ran. COLLAPSING it at the finish line snatches away the thing the
                // user was reading — the transcript actively loses content at the moment the work
                // completes, which is the opposite of what finishing should mean.
                //
                // A tool's output is an echo of something already on disk, so it collapses to its
                // one-line summary. A worker's output IS the answer that was asked for.
                //
                // FAILURE DOES NOT EXPAND. The case for expanding it — the error and the buttons
                // that act on it must be visible together — depends on inline buttons this row does
                // not have (see the NO INLINE BUTTONS note below); what is left is an ordinary
                // failed tool call. A read_file that missed is a one-line fact, and expanding it
                // prints the same error twice, header and body, on every miss. The header already
                // says `failed`, and `expand…` is there for anyone who wants the detail.
                // EVERYTHING COLLAPSES, INCLUDING A SPAWN.
                //
                // "A worker stays expanded" holds only for a STREAMING worker, whose prose has been
                // visible and growing for the whole run, so that collapsing at the finish line
                // snatches away what the user is reading. A sub-agent is the opposite case — its
                // work is BUFFERED and was never on screen (D22), so there is nothing to snatch.
                // What lands at its finish line is a wall of new text the user did not ask to have
                // opened.
                //
                // Seen in a screenshot: a completed spawn opened its whole envelope into the
                // transcript, pushing the parent's own answer — the thing the user was waiting for —
                // down the screen behind it. The answer is the parent's reply; the child's report is
                // the working, and working belongs one keypress away.
                // EXCEPT A PLAN, which is the case the rule above was not written for. It collapses
                // everything because a finished spawn dumps a wall of buffered prose that shoves the
                // parent's own answer off screen — but a plan is six short lines, and it IS the
                // answer to the call that produced it. Collapsing it to "plan · 3/3 · expand…" hides
                // the list at the exact moment the model just finished rewriting it.
                //
                // Claude Code pins its list to the bottom of the screen instead. This transcript has
                // no pinned region, so an expanded row in place is the nearest thing: the plan is
                // visible where it changed, and every revision stays in the scrollback in order.
                // OPENED, NOT MERELY LEFT ALONE. A row is created COLLAPSED, so "do not collapse
                // it" is not the same instruction as "open it" — a body can be rendered correctly
                // and still show only its header, because nothing on the path ever called
                // SetExpanded(true). The compact branch above does the opening for ordinary rows,
                // and a row exempted from being compact does not take that branch.
                //
                // llm_agent is excluded for the reason stated at the auto-expand site: a worker's
                // body grows under the reader, so it must stay one keypress away even though it is
                // also "the answer".
                _chat.SetExpanded(id, ExpandOnFinish(job));
            }

            // NO INLINE BUTTONS. Retry/Skip/Diagnose controls on a row invite the user to drive the
            // scheduler by hand while the orchestrator is mid-drive, which puts "a drive operation is
            // already in progress; drive operations must not overlap" on screen, and a hand-skipped
            // job desynchronises the plan from what the orchestrator believes has run.
            // The failure message and its reason stay -- they are what the model reads on the next
            // consult, and letting it re-plan is both more reliable and the whole point of a loop
            // that already has a repair round.
            _chat.ClearActions(id);

            // REFRESH THE DEPENDENTS. ToolUpdated fires only for the job that transitioned, so a job
            // BLOCKED by this failure would never re-render and would sit on "pending" forever —
            // exactly what a live screenshot showed ("Save the review report — pending" under a
            // failed dependency, with nothing saying it would never run). Their state has not
            // changed, but what their status row should SAY has.
            if (job.State is JobState.Failed or JobState.Cancelled)
                foreach (var (otherId, otherJob) in _known)
                    if (otherJob.DependsOn.Contains(job.Id)
                        && _lines.TryGetValue(otherId, out var otherLine))
                        _chat.SetStatus(otherLine, StatusText(otherJob), SeverityFor(otherJob.State));
        });

    /// <summary>
    /// The message body for a finished job: the model's text / the command's stdout, with the other
    /// output keys suppressed.
    ///
    /// <para>Deliberately NOT <c>JobDigest.RenderOutput</c>, which flattens every key as
    /// "key: value" — that shape exists for the ORCHESTRATOR, which needs exit codes and truncation
    /// flags to decide what to do next. A human reading the transcript wants the answer, not the
    /// envelope. The envelope is still on the status row (state, duration, error).</para>
    ///
    /// <para>Returns null when there is nothing to show, leaving the title in place rather than
    /// blanking the message.</para>
    /// </summary>
    /// <summary>The job's UNCLIPPED output text, for counting. <see cref="BodyFor"/> clips for
    /// display; a size summary must measure what actually came back.</summary>
    private static string? RawContent(Job job)
    {
        if (job.Result?.Output is not { } output) return null;
        return output.TryGetValue("content", out var c) ? c?.ToString() : null;
    }

    public static string? BodyFor(Job job) => BodyFor(job, forDisplay: true);

    /// <summary>
    /// A job's body — the expanded block's content.
    /// </summary>
    /// <param name="forDisplay">
    /// True for what the user READS; false for what the row is MEASURED by.
    ///
    /// <para>The two diverged when a shell body gained its "$ command" preamble: that is presentation,
    /// and folding it into the measurement made "echo hello" — a one-line result that belongs inline —
    /// report "5 lines, 34 chars" and open an expandable block over nothing. The decoration must not
    /// decide whether the thing it decorates is worth decorating.</para>
    /// </param>
    public static string? BodyFor(Job job, bool forDisplay)
    {
        // STRIPPED ONCE, AT THE EXIT. A command's output is arbitrary bytes, and the agent runs
        // binaries it has just built — a live session ran `cxgpu --gpu-usage --color` and its 24-bit
        // colour codes smeared the transcript. Doing it here rather than at each `return` is not
        // tidiness: this method has four exits through two branches, and the one that gets forgotten
        // is the one that ships.
        //
        // forDisplay ONLY. The measuring path feeds row sizing, and the model's own tool result is
        // untouched elsewhere — an agent verifying colour output by counting escapes needs to see
        // them, which is exactly what one drive did.
        var body = BodyText(job, forDisplay);
        return forDisplay ? TerminalText.Strip(body) is { Length: > 0 } s ? s : null : body;
    }

    private static string? BodyText(Job job, bool forDisplay)
    {
        var output = job.Result?.Output;

        // A failed job's REASON is the body — the user's next action depends on it, and the status
        // row truncates a stack trace or multi-line stderr into unreadability.
        if (job.State == JobState.Failed)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(job.Result?.ErrorMessage))
                parts.Add(job.Result!.ErrorMessage!);

            // STDERR, which is the whole point. A failed shell job's ErrorMessage is literally
            // "command exited with code 1" (ShellJobExecutor.cs:49) — the actual reason lives in
            // Output["stderr"] and was being dropped, so the body said nothing the status row had
            // not already said. Whatever the command printed to stderr is what the user needs.
            if (output is not null
                && output.TryGetValue("stderr", out var err)
                && err?.ToString() is { } errText && !string.IsNullOrWhiteSpace(errText))
                parts.Add(errText.TrimEnd());

            // Partial output too: a command that printed work before dying tells you HOW FAR it got.
            if (output is not null
                && output.TryGetValue("content", out var partial)
                && partial?.ToString() is { } partialText && !string.IsNullOrWhiteSpace(partialText))
                parts.Add(partialText.TrimEnd());

            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }

        if (output is null) return null;
        if (!output.TryGetValue("content", out var content)) return null;

        // A SHELL BODY LEADS WITH ITS COMMAND. The header carries it too, but the header is one line
        // and a command is not: "for i in 1 2 3; do echo …" truncates at the width of the row, so the
        // one thing the output cannot be read without is the thing that gets clipped. Expanding is
        // the moment the user wants the detail — showing what RAN above what it printed is the whole
        // point of opening it.
        //
        // Shell only. A read_file's path fits the header and repeating it would be noise; a worker's
        // body is prose it composed, and prefixing that with its own invocation reads as machinery.
        if (forDisplay
            && job.JobType == "shell"
            && job.Parameters.Get<string>("command", "") is { Length: > 0 } command)
        {
            var ran = $"$ {command.TrimEnd()}";
            var printed = content?.ToString();

            return string.IsNullOrWhiteSpace(printed) ? ran : ran + "\n\n" + printed;
        }

        var text = content?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// UNCLIPPED. Capping the body at a few thousand chars with a visible marker would answer a real
    /// worry — a long transcript makes the conversation unnavigable — but the row is COLLAPSED by
    /// default, so length costs nothing until the user opens it, and a clipped body puts the full
    /// text only in the log file. Kept as a pass-through rather than removed so the call sites and
    /// their tests keep their shape.
    /// </summary>
    public static string Clip(string text) => text;

    

    /// <summary>
    /// Deliberately a no-op. A CPU/memory sample arrives per running job on a timer; rendering each
    /// one would rewrite the status row several times a second and make the transcript unreadable.
    /// The side panel could afford that because it had dedicated space — inline, the job's outcome is
    /// what matters. Resource data is still collected and still reaches the log file.
    /// </summary>
    /// <summary>
    /// THE HEADER, AND NOTHING ELSE — the whole point of not routing a tick through ToolUpdated, which
    /// calls SetExpanded(id, true) and UpdateMessage(id, "") on every invocation.
    ///
    /// <para>Silently ignores a job with no row yet: a progress tick is not the add path, and adopting
    /// an unknown job here would race ToolsChanged into drawing the row twice.</para>
    /// </summary>
    public void ToolProgressed(Job job) =>
        _system.EnqueueOnUIThread(() =>
        {
            _known[job.Id] = job;
            if (!_lines.TryGetValue(job.Id, out var id)) return;

            _chat.SetHeader(id, CompactHeader(job));

            // THE BODY TOO, when there is one — but ONLY the body. This is still not ToolUpdated: that
            // method force-expands the row (SetExpanded(id, true)) and blanks it, and doing either
            // once a second would re-open a row the user collapsed and fight them for it.
            //
            // UpdateMessage alone changes what is BEHIND the expand without touching whether it is
            // open. A user who opened the row watches it fill; a user who left it shut sees nothing
            // move, which is the whole point of the distinction.
            if (!string.IsNullOrEmpty(job.ProgressBody))
                _chat.UpdateMessage(id, job.ProgressBody);
        });

    /// <summary>
    /// Re-renders the header of every row still running. Called once a second from the window's
    /// panel clock.
    ///
    /// <para>THE ELAPSED CLOCK HAS NO OTHER DRIVER. CompactHeader is a pure projection of a Job, so
    /// the "now - StartedAt" it computes is frozen at whatever moment last pushed a header. Progress
    /// reports push one, which is why a streaming sub-agent's clock looked fine — but a plain shell
    /// call reports nothing at all between its start and its result, so its clock was stamped once
    /// and never again. The inline [spinner] in that header is NOT a driver either: it is resolved
    /// out of the stored string at parse time, so it animates a header nobody has recomputed.</para>
    ///
    /// <para>NOT ROUTED THROUGH ToolUpdated, exactly like <see cref="ToolProgressed"/> and for
    /// exactly its reason: that method force-expands the row and blanks its body, and doing either
    /// once a second would re-open a row the user collapsed and fight them for it. SetHeader and
    /// UpdateMessage change what a row SAYS without touching whether it is open.</para>
    ///
    /// <para>THE BODY ONLY FOR A LIVE WORKER, which has a spinner and a clock of its own inside the
    /// table and therefore needs the same driver the header's clock needs. Every other running row's
    /// body is pushed by whatever produced it — a tick knows nothing new about that work, only about
    /// the time.</para>
    ///
    /// <para>Terminal jobs are skipped: their header carries a fixed duration off JobResult and
    /// rewriting it every second would be pure churn for an unchanging string.</para>
    /// </summary>
    public void RefreshRunningHeaders() => _system.EnqueueOnUIThread(() => RefreshRunningHeadersNow());

    /// <summary>
    /// The tick's actual work, split out so it is reachable without a running UI loop.
    ///
    /// <para>PUBLIC BECAUSE THE ALTERNATIVE IS NOT TESTING IT. Every other path in this sink is
    /// verified through <c>CompactHeaderForTest</c>, a pure projection — and a pure projection
    /// cannot catch a clock that never ticks, because the projection is right every time and the
    /// failure is that nobody re-runs it. The enqueue in the caller above puts the work behind a
    /// queue only the framework's own loop drains, so splitting the marshalling from the work leaves
    /// one method a test can call directly and watch the rows change.</para>
    ///
    /// <para>Returns how many rows it rewrote, so a test can distinguish "ticked nothing" from
    /// "ticked and the header happened not to change" — without that count, a clock that never ticks
    /// looks the same as one whose rows had nothing to change. Callers in the app ignore it.</para>
    ///
    /// <para>Must be on the UI thread; <see cref="RefreshRunningHeaders"/> is what guarantees it.</para>
    /// </summary>
    public int RefreshRunningHeadersNow()
    {
        var refreshed = 0;

        foreach (var job in _known.Values)
        {
            if (IsTerminal(job.State)) continue;
            if (!_lines.TryGetValue(job.Id, out var id)) continue;

            _chat.SetHeader(id, CompactHeader(job));

            // AND A LIVE WORKER'S BODY, which has its own spinner and its own clock and so needs the
            // same driver the header's clock needs — for the same reason, spelled out above: the
            // table is a pure projection of the child's panel and the moment it was rendered, so it
            // freezes unless something re-renders it.
            //
            // UpdateMessage ALONE, never ToolUpdated: this must change what is BEHIND the expand
            // without touching whether it is open, or a tick would re-open a row the user shut, once
            // a second, forever. The same distinction ToolProgressed is built on.
            if (RunningWorkerBody(job) is { } live) _chat.UpdateMessage(id, live);

            refreshed++;
        }

        return refreshed;
    }

    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }

    /// <summary>
    /// A chunk of a worker's generated text, appended to its message as it arrives.
    ///
    /// <para>Without this a long worker showed a spinner and then a wall of text in one step — a
    /// 90-second reviewer looked identical whether it was producing real analysis or spinning
    /// uselessly. The spinner says SOMETHING is happening; it cannot say what.</para>
    ///
    /// <para>Appends rather than rewrites: <c>Append</c> touches only the tail, where
    /// <c>UpdateMessage</c> re-renders the whole body on every chunk, which on a fast local model is
    /// hundreds of full re-renders per response.</para>
    ///
    /// <para>The row is switched OUT of compact mode on the first delta: it now has real content, so
    /// it needs its body shown and the collapse affordance back. Cheap to re-apply per chunk, and
    /// doing it here rather than guessing at plan time means a worker that returns nothing never
    /// pays for a block it does not use.</para>
    /// </summary>
    public void ToolOutputAppended(string jobId, string delta) =>
        _system.EnqueueOnUIThread(() =>
        {
            if (!_lines.TryGetValue(jobId, out var id)) return;

            if (_streaming.Add(jobId))
            {
                _chat.SetCompactFooter(id, false);
                _chat.SetExpanded(id, true);
            }

            _chat.Append(id, delta);
        });

    /// <summary>Jobs that have streamed at least one chunk, so the compact-mode switch happens once
    /// rather than on every delta.</summary>
    private readonly HashSet<string> _streaming = new();

    /// <summary>
    /// The header label. "Job" was hardcoded, and it is the wrong word for most of what appears: the
    /// orchestrator plans an <c>llm_agent</c> for everything, including "Read HeuristicEngine.cs",
    /// which is a WORKER calling read_file internally. The user's own reading of the screen — "I
    /// think all of those are tools" — is the correct one.
    ///
    /// <para>"Worker" for an llm_agent (a model doing delegated thinking), "Tool" for the mechanical
    /// executors. Based on the executor TYPE and nothing else, because the author is fixed when the
    /// message is CREATED — before the job has run — so it cannot depend on what the job later
    /// produced. Verified: ChatTranscriptControl exposes no SetAuthor.</para>
    /// </summary>
    /// <summary>Test seam: the header label is a pure projection of the job.</summary>

    /// <summary>Every job belongs in the transcript — no job type is filtered out.</summary>
    private static bool ShouldShow(Job job) => true;

    public static string AuthorForTest(Job job) => AuthorFor(job);

    private static string AuthorFor(Job job)
    {
        // Compression is not a tool the model called — it is housekeeping the app did to its own
        // context, and labelling it "Tool" would put it among the model's actions as though it had
        // chosen to run it.
        if (job.JobType == "compress") return "Context";

        if (job.JobType != "llm_agent") return "Tool";

        // Name the ROLE when the plan specified one — "Worker · reviewer" says which of the four
        // built-ins (planner/implementer/reviewer/debugger) actually ran, which is the difference
        // between "a model looked at this" and "the reviewer looked at this". The role is a job
        // PARAMETER rather than a field on Job, so it is read defensively: JobParameters.Get<T>
        // indexes and throws on a missing key, and a header must never kill a render.
        var role = job.Parameters.Values.TryGetValue("role", out var r) ? r?.ToString() : null;
        return string.IsNullOrWhiteSpace(role) ? "Worker" : $"Worker · {role}";
    }

    /// <summary>
    /// The single line a mechanical step gets. Research across terminal agents (see
    /// .superpowers/sdd/tool-ui-research.md) found the dominant pattern is a ONE-LINE call with its
    /// result folded in — Claude Code prefixes the call with <c>⏺</c> and the result with <c>⎿</c>,
    /// Gemini CLI's compact mode renders "status + description" on one line. Nobody spends a
    /// bordered block on a file read.
    ///
    /// <para>Measured on a live drive against a real repo, cxagent's tool bodies were: "20" (2
    /// chars), "README.md exists" (16), "" (0), "MIT License" (11), "# MimeKit" (9). Six of seven
    /// were under 30 characters, and every one cost FIVE lines — header, body, a full-width rule,
    /// a status row and a blank — the same as an 800-character code review.</para>
    ///
    /// <para>Anthropic's own users filed this: issue #39683 asks for default-collapsed tool output
    /// because "the signal gets buried in the noise", and #26968 for truncation of payloads that
    /// run to "hundreds of lines for a simple page". Both are unresolved, so this is an unmet need
    /// across the space rather than a solved problem to copy verbatim.</para>
    /// </summary>
    private const int MaxInlineResultChars = 60;

    /// <summary>
    /// A one-line row: the step's name, then its result folded in after <c>⎿</c> when that result
    /// is short enough to fit. Returns null when the result is too long to inline — that job keeps
    /// its expandable block, which is the whole point of the distinction.
    /// </summary>
    /// <summary>
    /// The header line for a compact step: its name plus the state, folded in here rather than given
    /// a whole status row of its own — and, while running, an inline <c>[spinner]</c> so the row does
    /// not change SHAPE when it finishes, it just stops spinning.
    ///
    /// <para>This is what takes a tool step from three lines to two. The status row is the third
    /// line, and everything on it ("done · 0.0s") fits beside the name.</para>
    ///
    /// <para>Markup IS honoured here, unlike the message body: the header is not routed through the
    /// markdown pipeline, so a [spinner] tag renders as a spinner rather than as the literal text
    /// "[spinner]" — the trap that caught the ⎿ separator earlier.</para>
    /// </summary>
    /// <summary>
    /// The decision badge for a row — <c>"  ·  auto-approved"</c>, <c>"  ·  auto-denied"</c>, or
    /// empty. Leading separator, so a caller can append it without testing for emptiness.
    ///
    /// <para>WHO DECIDED, when it was not the user and not a silent rule. DecidedBy is "auto" only
    /// on the two paths a classifier actually ruled on (PermissionOutcome.AutoAllow /
    /// ByClassifier) — never on a stored-rule or in-boundary silent pass, and never on a prompt the
    /// user answered. Those two are the ordinary cases and stay unbadged: a prompt the user just
    /// answered needs no badge (they were there), and trusting a folder is what "silent" means.
    /// Only the surprising ones — the user was NOT asked and it was not a rule either — earn a
    /// word.</para>
    ///
    /// <para>READ FROM THE JOB, NOT FROM ITS RESULT, and that is the timing fix rather than a
    /// tidy-up. A JobResult exists only once the call has FINISHED, so a badge sourced there could
    /// not appear before then — while the fact it reports is settled at the permission gate,
    /// BEFORE the tool runs. An auto-DENIED tool never executes at all, so its refusal had no
    /// result to be read off.</para>
    ///
    /// <para>DENIED IS READ FROM THE STATE, which is why this takes the whole job: "auto" names the
    /// decider, not the verdict. A running row has not failed yet and reads as approved — correct,
    /// because a denial does not run: it fails immediately and the row is terminal by the time
    /// anyone sees it.</para>
    /// </summary>
    /// <summary>
    /// The word for a decision the USER did not make, or "" for every ordinary one.
    ///
    /// <para>NO SEPARATOR OF ITS OWN. Return "  ·  auto-approved" here and CompactHeader appends
    /// its own "  ·  " after it, so a badged row renders "· · auto-approved · done", doubled — seen
    /// on a live drive. The separators belong to the chain that assembles the header, not to the
    /// pieces it joins.</para>
    /// </summary>
    private static string Badge(Job job) =>
        job.DecidedBy == "auto"
            ? (job.State == JobState.Failed ? "auto-denied" : "auto-approved")
            // REVIEWING TAKES OVER ONLY WHILE NO VERDICT EXISTS YET, and only on a row still
            // running — DecidedBy above always wins once it is set, which is what makes this word
            // disappear the instant the classifier rules rather than sit beside its badge. Guarded
            // on IsTerminal defensively: Job.Reviewing is set false before the gate either returns a
            // verdict or falls through to a prompt, so a terminal row should never see it true, but
            // a badge read off a finished job must never show a word that means "still deciding".
            : job.Reviewing && !IsTerminal(job.State)
                ? "reviewing…"
                : "";

    /// <summary>
    /// " · 2.0s" for anything under a minute, " · hh:mm:ss" at or past one — or "" when the job has
    /// no duration yet. Shared by CompactHeader's finished branch and StatusText, both of which
    /// showed the same defect: rendering `{TotalSeconds:0.0}s` unconditionally reads "660.0s" for an
    /// eleven-minute build, a number nobody can parse at a glance without doing the division
    /// themselves. SIXTY SECONDS is the threshold rather than something larger because short
    /// durations are compared AT A GLANCE against neighbouring rows ("2.0s" vs "5.0s") and that
    /// habit should not change just because a few of them are now formatted differently — the switch
    /// only needs to happen once tenths-of-a-second precision has stopped being the useful unit,
    /// which is exactly at a minute.
    /// </summary>
    private static string DurationSuffix(TimeSpan? duration) =>
        duration is not { } d ? ""
        : d.TotalSeconds < 60 ? $" · {DisplayNumber.Fixed(d.TotalSeconds, 1)}s"
        : $" · {d:hh\\:mm\\:ss}";

    /// <summary>Test seam: the header is a pure projection of the job.</summary>
    public static string CompactHeaderForTest(Job job) => CompactHeader(job);

    private static string CompactHeader(Job job)
    {
        var author = AuthorFor(job);

        // ESCAPED, because the terminal branch below wraps this in a colour scope. DisplayName is a
        // tool call with its raw JSON arguments — `read_file {"path":"/x"}` — and single-agent rows
        // routinely contain '['. Unescaped inside a scope that is a tag the parser tries to read,
        // and the row renders as an EMPTY LINE rather than erroring: the failure is silent and the
        // information is simply gone.
        var name = SharpConsoleUI.Parsing.MarkupParser.Escape(Title(job));

        if (!IsTerminal(job.State))
        {
            // LIVE PROGRESS BELONGS HERE, NOT IN StatusText. A running row always takes the compact
            // branch, which calls ClearStatus immediately after setting this header — so StatusText's
            // "running…" is never on screen at all. Someone changing StatusText would test it through
            // its own seam, see nothing, and lose an afternoon.
            //
            // ESCAPED like `name` above, and for the same reason: this text is wrapped in a scope the
            // markup parser reads, and an unescaped '[' renders the whole row as an EMPTY LINE
            // rather than erroring.
            var progress = string.IsNullOrWhiteSpace(job.ProgressMessage)
                ? ""
                : "  ·  " + SharpConsoleUI.Parsing.MarkupParser.Escape(job.ProgressMessage);

            // ON THE RUNNING ROW, which is the whole reason the badge moved onto the Job. The
            // classifier rules at the gate, before the tool starts; a `dotnet build` it approved
            // then runs for minutes, and learning afterwards that a model waved it through is
            // strictly worse than seeing it while it happens. Same word, same slot as the finished
            // row below, so the row does not change shape when it settles.
            // ITS OWN SEPARATOR, because this branch has no "  ·  " chain to slot into — the running
            // row is "author  name" with no state or duration after it yet. The terminal branches
            // below build that chain themselves, so Badge() carries no separator of its own and each
            // caller adds exactly the one its shape needs.
            var deciding = Badge(job) is { Length: > 0 } word ? "  ·  " + word : "";

            // ELAPSED, AT THE END OF THE HEADER — sourced from StartedAt rather than a second timer.
            //
            // THE SPINNER DOES NOT TICK THE CLOCK, however much it looks like it should. The tag
            // below is resolved when this STRING is parsed, not when it is produced:
            // MarkupSpinnerClock hands the parser a glyph derived from elapsed monotonic time, and
            // its InvalidateForInlineSpinner asks the host to REPAINT — which re-parses the stored
            // header text and picks a new glyph out of it. Nothing in that loop calls back into
            // CompactHeader, so relying on it leaves "now" at whatever it was the last time something
            // pushed a header, and a row with no progress reports (a plain `run_shell` is exactly
            // that) pushes exactly one — the number then sits frozen on screen indefinitely.
            //
            // What ticks it is RefreshRunningHeaders below, driven off the window's existing
            // one-second panel clock. One second is the correct cadence for an hh:mm:ss field and
            // the spinner's own interval is far faster and belongs to the framework.
            //
            // NO CLOCK WHILE THE GATE IS STILL DECIDING. Reviewing means work has not begun, so
            // there is no runtime to report — and every candidate for what to show instead is worse
            // than showing nothing. A live count would be the very review time that is deliberately
            // kept OUT of the number (see Agent.InvokeAndShowAsync); a frozen 00:00:00 reads as a
            // hung clock, which is exactly the complaint this whole clock exists to avoid. The badge
            // in this slot already says "reviewing…", so the row is not silent about why the clock
            // is absent, and the clock appearing at zero is then an honest signal that the work
            // itself has started.
            //
            // A REVIEW TIMER, IF ONE IS EVER WANTED, NEEDS NOTHING BUILT HERE — noted only so that
            // nobody closes the door by tidying up. The two stamps are already distinct: CreatedAt is
            // when the row appeared and StartedAt is when the work began, so the review window is
            // exactly `StartedAt - CreatedAt` once the gate has ruled, and `UtcNow - CreatedAt` while
            // it is still deciding. Deliberately not built: it would be timing something the user did
            // not ask about and cannot act on, next to a badge that already says what is happening.
            // Collapsing the two stamps back into one would cost that, silently.
            //
            // StartedAt is also null in the instant between a row being created and its first stamp
            // — guarded so that instant shows no clock rather than a negative or garbage one.
            var elapsed = !job.Reviewing && job.StartedAt is { } started
                ? "  ·  " + (DateTimeOffset.UtcNow - started).ToString(@"hh\:mm\:ss")
                : "";

            // Braille (⣷⣯⣟⡿⢿⣻⣽⣾) — the user's pick. Single-cell like Arc, so the text after it does
            // not shift as it animates (Dots is three columns wide and does exactly that).
            //
            // THE INTERVAL IS EXPLICIT, and it matches Braille's own default rather than overriding
            // it. What actually made the spinner crawl was not the interval at all: CollapsiblePanel
            // parses its own header markup and was never registered as an inline-spinner host, so the
            // clock ticked on time and had nobody to invalidate — the glyph advanced only when the
            // app's one-second panel clock happened to dirty the window. Fixed in the framework.
            //
            // Stated here anyway because the clock ticks at the SHORTEST interval any parsed tag
            // reports, so this is the app declaring the repaint cadence it wants rather than
            // inheriting whatever else is on screen — and it is also what makes `elapsed` above tick
            // without a dedicated clock of its own.
            return $"[spinner braille {SpinnerIntervalMs}] {author}  {name}{deciding}{progress}{elapsed}";
        }

        var duration = DurationSuffix(job.Result?.Duration);
        var state = job.State switch
        {
            JobState.Succeeded => "done",
            JobState.Skipped => "skipped",
            JobState.Cancelled => "cancelled",
            _ => job.State.ToString().ToLowerInvariant(),
        };

        // BEFORE THE STATE, slotting into the same "· x · y" chain CompactHeader already builds —
        // "done · 0.0s" becomes "auto-approved · done · 0.0s" rather than a second, differently
        // shaped suffix.
        var decidedBy = Badge(job) is { Length: > 0 } b ? b + "  ·  " : "";

        // A GLYPH where the spinner was, not nothing. Replacing the spinner with an empty string
        // shifts the whole row one cell left the instant a step finishes, so a column of steps
        // jitters as each completes. A static mark holds the column and reads as "settled".
        var mark = job.State == JobState.Succeeded ? "✔" : "•";

        // FINISHED WORK RECEDES. A completed row is history — it stays legible, and stops competing
        // for attention with the one row still running. Without this a screen of twenty finished
        // tool calls is twenty things all shouting equally, and the live one is lost among them.
        // Completed rows drop to textMuted while active rows hold theme.text, and that is the single
        // mechanic that makes a long session readable.
        //
        // A FAILURE IS RED, not merely un-muted. "Does not recede" was implemented as "is not grey",
        // which left it the same colour as ordinary text — so the one finished row the user still has
        // to act on looked exactly like the nineteen that succeeded.
        //
        // THE MARK AND THE STATE, not the whole row: the tool's name and its arguments are still just
        // information, and colouring a long JSON blob red makes the row harder to read rather than
        // easier to spot. The two ends carry it.
        if (job.State == JobState.Failed)
            return $"[{ColorScheme.DangerMarkup}]{mark}[/] {author}  {name}  ·  "
                 + $"[{ColorScheme.DangerMarkup}]{decidedBy}{state}{duration}[/]";

        return $"[{ColorScheme.MutedMarkup}]{mark} {author}  {name}  ·  {decidedBy}{state}{duration}[/]";
    }

    /// <summary>Test seam: the folded row is a pure projection of the job.</summary>
    public static string? OneLineRowForTest(Job job) => OneLineRow(job);

    private static string? OneLineRow(Job job)
    {
        // MEASURED, not displayed: the shell preamble is presentation and must not push an otherwise
        // inline result into an expandable block. See BodyFor's forDisplay parameter.
        var body = BodyFor(job, forDisplay: false);
        if (body is null) return string.Empty;   // header says it all; no result line

        var trimmed = body.Trim();

        // The header carries the NAME and the state (CompactHeader), so the body carries only the
        // RESULT — repeating the name here printed it twice, once per mechanism. Seen live:
        //     Tool  Read Base64Decoder.cs  ·  done · 0.0s
        //     Read Base64Decoder.cs  <corner>  97 lines, 4,045 chars
        if (trimmed.Contains('\n') || trimmed.Length > MaxInlineResultChars)
        {
            // BULKY TOOL OUTPUT: summarise, never dump. A file read echoed 12KB of source into the
            // transcript behind an "expand…" — content the user already has on disk. What they want
            // to know is that it was read and roughly how much came back; a size says that. A WORKER
            // is exempt: its long output is the prose it composed, the answer that was asked for, so
            // it keeps its expandable block. That is the whole tool-vs-worker distinction.
            // A WORKER'S LONG OUTPUT KEEPS ITS EXPANDABLE BLOCK rather than being summarised by
            // size: it is prose the model composed, not an echo of something already on disk. A PLAN
            // is the same case — "6 lines, 134 chars" measures the list instead of showing it, and
            // the list is the entire reason the row exists.
            if (IsTheAnswer(job)) return null;

            // A FAILURE IS NEVER SUMMARISED BY SIZE. The count answers "how much came back", which is
            // the right question for a bulky success and meaningless for an error — a missing file
            // rendered as "1 lines, 68 chars", measuring the reason instead of stating it. The first
            // line carries the cause; later lines are usually stack frames or a stderr tail, and they
            // stay in the expandable body.
            if (job.State == JobState.Failed)
            {
                var first = trimmed.Split('\n')[0].Trim();
                return first.Length > MaxInlineResultChars
                    ? $"⎿  {first[..MaxInlineResultChars]}…"
                    : $"⎿  {first}";
            }

            // Count the RAW output, not the clipped body. BodyFor clips to MaxBodyChars before
            // returning, so counting `trimmed` reported the size of MY OWN TRUNCATION — every row
            // read "4,04x chars" regardless of the file, because that is what 4,000 chars of C#
            // plus an elision marker measures. A 986-line, 36,666-char file was announced as
            // "96 lines, 4,045 chars".
            //
            // That is worse than a cosmetic error: it is the ONE number telling the user how much
            // came back, and it was silently reporting a constant.
            var raw = RawContent(job) ?? trimmed;
            var lineCount = raw.Count(c => c == '\n') + 1;
            return $"⎿  {DisplayNumber.Grouped(lineCount)} lines, {DisplayNumber.Grouped(raw.Length)} chars";
        }

        // A SPAWN HAS NO RESULT LINE. Its body is the envelope — <sub_agent id=… state=…> and the
        // child's report — and none of that belongs on a one-line summary beneath the header.
        //
        // Seen in a screenshot: a finished spawn rendered its envelope's opening tag as the result
        // line, so the row read the id and `state="completed"` back at a user whose header already
        // said `done · 144.6s`. Two lines, one fact, and the noisier of the two was the machine's.
        //
        // KEYED ON THE ENVELOPE, NOT ON JobType. llm_agent is the row TYPE and covers every
        // worker; suppressing all of them broke short tool results, which legitimately fold their
        // whole output into this line ("20", "MIT License"). What has nothing to say here is
        // specifically a machine-readable envelope addressed to the parent's model.
        if (trimmed.StartsWith("<sub_agent", StringComparison.Ordinal)) return null;

        // NO markup here. The Tool role sets Markdown = true, so its content routes through
        // MarkdownToMarkup, which ESCAPES '[' — a "[dim]" tag renders as the literal text "[dim]"
        // (verified live: the row read "Count .cs files  [dim]⎿  20"). The same trap MainWindow.cs:86
        // documents for System lines, hit from the other direction.
        return $"⎿  {trimmed}";
    }

    /// <summary>
    /// Removes the <c>&lt;sub_agent&gt;</c> wrapper, leaving what the child actually said.
    ///
    /// <para>The tags carry the id and the state for the parent's MODEL to read. A person reading the
    /// transcript has the header for state and does not need the id — and the tag being the body's
    /// first line meant it became the collapsed preview, which is the one line a reader sees without
    /// asking for more.</para>
    ///
    /// <para>Only the wrapper goes. A capped or stuck run carries a NOTE between the tag and the text
    /// ("This agent hit its turn limit… NOT a completed answer") and that is exactly the sort of thing
    /// a person needs, so it stays.</para>
    /// </summary>
    private static string? StripEnvelope(string? body)
    {
        if (body is null || !body.StartsWith("<sub_agent", StringComparison.Ordinal)) return body;

        var open = body.IndexOf('>');
        if (open < 0) return body;

        var inner = body[(open + 1)..];
        var close = inner.LastIndexOf("</sub_agent>", StringComparison.Ordinal);
        if (close >= 0) inner = inner[..close];

        return inner.Trim() is { Length: > 0 } text ? text : null;
    }

    private static string Title(Job job) =>
        string.IsNullOrWhiteSpace(job.DisplayName) ? job.JobType : job.DisplayName;

    /// <summary>
    /// The status line: state, plus duration once there is one and the error when it failed. The
    /// error is NOT truncated here — a job's failure message is the whole reason the user is looking.
    /// </summary>
    /// <summary>Test seam: the status row is a pure projection worth pinning, but the rendering it
    /// drives is only observable through a UI queue the tests cannot drain.</summary>
    public static string StatusTextForTest(Job job) => new InlineJobSink(
        new ConsoleWindowSystem(new SharpConsoleUI.Drivers.HeadlessConsoleDriver(80, 24),
            new SharpConsoleUI.Configuration.ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
        new ChatTranscriptControl()).StatusText(job);

    /// <summary>
    /// Every job the sink has been told about, by id — ToolsChanged/ToolUpdated both record here. Needed
    /// only to explain a
    /// BLOCKED job — a job whose dependency failed stays Pending forever (DagScheduler.DepsMet
    /// requires every dependency Succeeded or Skipped), so "pending" is honest and useless: a live
    /// screenshot showed "Save the review report — pending" under a failed dependency, with nothing
    /// saying it would never run.
    /// </summary>
    private readonly ConcurrentDictionary<string, Job> _known = new();

    /// <summary>Names the dependencies that are blocking this job, or null when it is not blocked.</summary>
    private string? BlockedBy(Job job)
    {
        if (job.State != JobState.Pending) return null;

        var blockers = job.DependsOn
            .Select(id => _known.TryGetValue(id, out var d) ? d : null)
            .Where(d => d is { State: JobState.Failed or JobState.Cancelled })
            .Select(d => string.IsNullOrWhiteSpace(d!.DisplayName) ? d.JobType : d.DisplayName)
            .ToList();

        return blockers.Count == 0 ? null : string.Join(", ", blockers);
    }

    /// <summary>Milliseconds per spinner frame — Braille's own default; see the tag that uses it.</summary>
    private const int SpinnerIntervalMs = 100;

    private string StatusText(Job job)
    {
        var duration = DurationSuffix(job.Result?.Duration);

        return job.State switch
        {
            // NO error text here any more. The BODY now carries it (BodyFor), and appending it here
            // too printed the same sentence twice, stacked — verified in a live screenshot:
            //     Could not find file '/home/nick/source/cxlog/package.json'.
            //     failed · 0.0s — Could not find file '/home/nick/source/cxlog/package.json'.
            // The body is the better home: it wraps, it holds stderr the status row cannot, and it
            // sits directly above the Retry/Skip/Diagnose buttons. The status row's job is state and
            // duration — the envelope, not the message.
            JobState.Failed => $"failed{duration}",
            JobState.Succeeded => $"done{duration}",
            JobState.Running => "running…",
            JobState.Queued => "queued",
            // A Pending job whose dependency FAILED will never run — DepsMet requires every
            // dependency Succeeded or Skipped — so say that rather than leaving it looking like work
            // still to come.
            JobState.Pending when BlockedBy(job) is { } blockers
                => $"blocked — waiting on {blockers}, which failed",
            JobState.Skipped => "skipped",
            JobState.Cancelled => "cancelled",
            _ => job.State.ToString().ToLowerInvariant(),
        };
    }

    private static NotificationSeverity? SeverityFor(JobState state) => state switch
    {
        JobState.Failed => NotificationSeverity.Danger,
        JobState.Cancelled or JobState.Skipped => NotificationSeverity.Warning,
        JobState.Succeeded => NotificationSeverity.Success,
        _ => null,
    };

    /// <summary>
    /// Whether this job renders as a compact single line rather than a full collapsible block.
    ///
    /// <para>Driven by whether there is anything to READ, not by executor type. The type rule this
    /// replaces could not see the actual noise: the orchestrator plans an <c>llm_agent</c> for
    /// everything — "Read HeuristicEngine.cs" is a WORKER that calls read_file internally, not a
    /// <c>file</c> job — so five 0.0s rows still rendered as full blocks offering to expand into
    /// nothing. Seen in a live screenshot.</para>
    ///
    /// <para>A job with no body has nothing an expand affordance could reveal, whatever produced it.
    /// FAILURE always wins: a failed job shows its error and its Retry/Skip/Diagnose row.</para>
    /// </summary>
    /// <summary>Test seam for <see cref="IsCompactRow"/> — a pure decision worth pinning, whose
    /// rendering is only observable through a UI queue the tests cannot drain.</summary>
    public static bool IsCompactRowForTest(Job job) => IsCompactRow(job);

    /// <summary>Test seam for <see cref="ExpandOnFinish"/> — the decision is pure, its effect is
    /// only observable through a UI queue the tests cannot drain.</summary>
    public static bool ExpandOnFinishForTest(Job job) => ExpandOnFinish(job);

    /// <summary>
    /// Whether a finished row is OPENED, and the distinction that made this a method.
    ///
    /// <para>"DO NOT COLLAPSE IT" IS NOT "OPEN IT". A row is created collapsed, so an exemption that
    /// merely skips the SetExpanded(id, false) leaves the diff rendering perfectly while showing
    /// nothing but its header, because nothing on this path ever opens it. The compact branch above
    /// does the opening for ordinary rows, and a diff does not take that branch. That failure shows
    /// up in a live session, not in a test, which is why the decision has a name and a seam.</para>
    ///
    /// <para>A WORKER IS THE EXCEPTION AMONG THE ANSWERS. llm_agent output is buffered and lands all
    /// at once at the finish line: opening it puts a wall of text on screen the user did not ask
    /// for, and pushes the parent's own answer down behind it.</para>
    /// </summary>
    private static bool ExpandOnFinish(Job job) => IsTheAnswer(job) && job.JobType != "llm_agent";

    /// <summary>
    /// Rows whose body is the ANSWER rather than the working. They open when they finish, they are
    /// never folded into one line, and they are never summarised by size.
    ///
    /// <para>The rule was already written three times in this file before it had a name: "A tool's
    /// bulky output is an echo; a worker's is the answer", a plan is exempt because "the list IS the
    /// point of the row", and a worker keeps its block because its output is "prose the model
    /// composed, not an echo of something already on disk".</para>
    ///
    /// <para>A PREDICATE, NOT A REPEATED LITERAL. The set was spelled out at several separate
    /// decision points, and adding a member to each of them by hand is how one gets MISSED: the
    /// obvious collapse site is easy to find, the one guarded by IsCompactRow is not, and a row that
    /// renders and then collapses itself does so with every test still green.</para>
    ///
    /// <para>llm_agent is NOT part of this at the auto-expand site above: a worker's body grows under
    /// the reader for minutes, so it must not open by itself. That site keeps its own test.</para>
    /// </summary>
    private static bool IsTheAnswer(Job job) => job.JobType is "llm_agent" or "todo";

    /// <summary>
    /// Whether this row collapses to one line. Two ways to qualify, and the SECOND is the one the
    /// user found by looking at a real transcript:
    ///
    /// <para>(a) the result is short enough to fold into the row — "20", "README.md exists";</para>
    ///
    /// <para>(b) the result is a MECHANICAL dump: a tool echoing content the user already has. A
    /// read of Base64Decoder.cs put 12KB of source into the transcript behind an "expand…", and
    /// nobody wants a file they own pasted back at them — they want to know it was READ. My first
    /// rule treated "lots of content" as "worth showing", which is exactly backwards for a tool.
    /// Every project surveyed converges on the same fix for this case: show the command and a
    /// one-line summary, not the payload. (Claude Code #26968 on 1,000+ line MCP dumps; Pi #3114
    /// on 100+ line JSON "filling up the terminal screen"; Codex #21252.)</para>
    ///
    /// <para>A WORKER is exempt from (b): its long output is prose it composed — the answer the
    /// user asked for — not an echo of something already on disk. That is the whole distinction
    /// between a tool and a worker, and it is why the two are labelled differently.</para>
    ///
    /// <para>FAILURE IS NOT EXEMPT. Exempting it — "a failed job keeps its error whatever its size"
    /// — reserves a footer for inline buttons that do not exist (see the NO INLINE BUTTONS note
    /// above), and buys four lines to say one thing: the header already ends in "failed · 0.0s",
    /// then the error, then a full-width rule, then "failed · 0.0s" AGAIN. Measured live on a
    /// missing file.
    ///
    /// <para>A failed row is compact on the same terms as any other: the header carries the outcome
    /// and one line carries the reason. Nothing is hidden — the error is the body, and it is what
    /// the model reads on the next consult either way.</para>
    /// </summary>
    private static bool IsCompactRow(Job job)
    {
        if (OneLineRow(job) is not null) return true;

        // A tool's bulky output is an echo; a worker's is the answer. So is a plan: the list IS the
        // point of the row, and collapsing it to "plan · 2/5 · expand…" hides the thing the user
        // wanted to see at the one moment they asked for it.
        return !IsTheAnswer(job);
    }

    private static bool IsTerminal(JobState state) =>
        state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;

    // ---- the worker timetable ---------------------------------------------------------------------

    /// <summary>
    /// A child agent's finished tool calls, in the order they ran, keyed by the CHILD's agent id.
    ///
    /// <para>THE CHILD'S ID IS THE ONLY KEY THAT WORKS. A parent forwards its children's reports
    /// unchanged (Agent.cs, OnChildSpawned) precisely so a call is attributed rather than absorbed,
    /// so every report already carries the id of whoever made it. The parent's spawn JOB has a
    /// different id entirely — it identifies the call, not the agent that the call started.</para>
    ///
    /// <para>Concurrent because reports arrive on whatever thread the finishing call resumed on,
    /// which is the same reason <see cref="_lines"/> is; and it is READ from the UI thread when a
    /// worker's row closes.</para>
    ///
    /// <para>EMPTIED WHEN A WORKER FINISHES. Without that, a session that spawns thirty workers
    /// keeps every call any of them ever made for as long as the process lives.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, List<ToolCallReport>> _workerCalls = new();

    /// <summary>
    /// Records one finished tool call. Wired to <c>Agent.ToolCallFinished</c>, which a parent raises
    /// for its own calls AND for every one of its children's.
    ///
    /// <para>The parent's own calls accumulate here too and are never read: this is keyed by agent
    /// id and only a child's id is ever looked up. Filtering them out would need this to know which
    /// ids are children, which is exactly the knowledge the forwarding design refuses to centralise
    /// — and the parent's own list is bounded by the session, not by the number of workers.</para>
    /// </summary>
    public void RecordToolCall(ToolCallReport report)
    {
        var calls = _workerCalls.GetOrAdd(report.AgentId, _ => new List<ToolCallReport>());
        lock (calls) calls.Add(report);
    }

    /// <summary>
    /// The live child behind a RUNNING spawn row, keyed by the PARENT's job id.
    ///
    /// <para>THE PARENT'S JOB ID IS THE ONLY KEY AVAILABLE WHILE IT RUNS. <see cref="_workerCalls"/>
    /// keys by the CHILD's agent id, which the finished row reads back out of the envelope — and the
    /// envelope does not exist until the child has answered. The row on screen is addressed by the
    /// job id from the moment it appears, so that is what the live path can join on.</para>
    ///
    /// <para>Concurrent for the reason <see cref="_workerCalls"/> is: it is written from whatever
    /// thread built the child and read from the UI thread on every tick.</para>
    ///
    /// <para>RELEASED WHEN THE ROW CLOSES. A <see cref="SubAgent"/> pins a whole
    /// <see cref="CxAgent.Core.Agents.Agent"/> and its context, so a fan-out session that kept every
    /// one it ever spawned would hold every child's conversation for as long as the process lives —
    /// strictly worse than the call lists, which are a few hundred bytes each.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, SubAgent> _workerChildren = new();

    /// <summary>
    /// Associates a running spawn row with the child it started.
    ///
    /// <para>Wired to <c>Agent.ChildSpawned</c>, which fires the moment the child is built and
    /// BEFORE it runs — the row is already on screen by then, so the live body has a child to read
    /// from its first tick rather than from the first turn the child completes.</para>
    ///
    /// <para>NOT MARSHALLED ONTO THE UI THREAD, for the reason <see cref="RecordToolCall"/> gives:
    /// this writes to a concurrent dictionary and touches no control.</para>
    /// </summary>
    public void NoteChild(string jobId, SubAgent child) => _workerChildren[jobId] = child;

    /// <summary>Test seam for <see cref="RunningWorkerBody"/>, which reads the sink's own maps and
    /// so cannot be static.</summary>
    public string? RunningWorkerBodyForTest(Job job) => RunningWorkerBody(job);

    /// <summary>
    /// A RUNNING worker's body: the same timetable its finished row will show, growing as calls land,
    /// with whatever the child is doing right now as the last line.
    ///
    /// <para>Returns null when there is nothing to draw — not a worker, no child noted yet, or a
    /// child that has called nothing — and the caller then falls back to <c>ProgressBody</c>. Blanking
    /// it instead would mean expanding a running spawn reveals an empty block, which is the worst
    /// moment to show nothing.</para>
    ///
    /// <para>THE SAME RENDERER AS THE FINISHED ROW, so the row does not change SHAPE when it settles:
    /// what it gains at the finish line is the <c>out</c> column and the final counts, and nothing
    /// else moves. Two renderers would drift, and the drift would show up as the table visibly
    /// reflowing at the exact moment the user stops watching it.</para>
    /// </summary>
    private string? RunningWorkerBody(Job job)
    {
        if (job.JobType != "llm_agent") return null;
        if (!_workerChildren.TryGetValue(job.Id, out var child)) return null;

        // THE CHILD'S OWN ID, held rather than parsed. This is the same key RecordToolCall files
        // under, and while the child is running there is no envelope to read it back out of.
        var calls = _workerCalls.TryGetValue(child.Agent.Id, out var recorded) ? recorded : null;

        // THE DIRECT CHILD'S PANEL, AND NO DEEPER. A child may spawn its own children, and the
        // completed list below already includes their calls because ToolCallFinished is forwarded up
        // the chain. The in-flight row is different: it is a read of ONE panel, and walking into a
        // grandchild's would put another agent's work on this row with nothing saying whose it is.
        //
        // The first non-terminal job, not the last: a child runs its calls one at a time, so there is
        // at most one, and taking the first is what makes the absent case (all terminal) render as no
        // row rather than as a stale one.
        var inFlight = child.Jobs.Jobs.FirstOrDefault(j => !IsTerminal(j.State));

        var snapshot = calls is null ? [] : Snapshot(calls);
        if (snapshot.Count == 0 && inFlight is null) return null;

        return Timetable(snapshot, Elapsed(job), new LiveRun(inFlight));
    }

    /// <summary>
    /// That the run is STILL GOING, and what it is doing if it is doing anything — the difference
    /// between a live table and a settled one.
    ///
    /// <para>A RECORD RATHER THAN A NULLABLE JOB, and a test found the reason. Keying "is this live"
    /// on "is a call in flight" conflates two facts that come apart constantly: a child sits between
    /// calls on every hop, thinking or waiting on the provider, and a table keyed that way would grow
    /// and lose its <c>out</c> column several times over one run. Being present says the run is live;
    /// <see cref="InFlight"/> says whether there is a call to draw for it.</para>
    /// </summary>
    /// <param name="InFlight">The child's currently-executing job, or null between calls.</param>
    private sealed record LiveRun(Job? InFlight);

    /// <summary>A copy taken under the list's own lock, so the render walks a set nothing is
    /// appending to.</summary>
    private static List<ToolCallReport> Snapshot(List<ToolCallReport> calls)
    {
        lock (calls) return [.. calls];
    }

    /// <summary>
    /// How long the row has been running, off <c>StartedAt</c> rather than a second timer — the same
    /// source the header's clock uses, so the two numbers cannot disagree.
    /// </summary>
    private static TimeSpan Elapsed(Job job) =>
        job.StartedAt is { } started ? DateTimeOffset.UtcNow - started : TimeSpan.Zero;

    /// <summary>Test seam: the table is a pure projection of the calls and the run's length.</summary>
    public static string TimetableForTest(IReadOnlyList<ToolCallReport> calls, TimeSpan ran) =>
        Timetable(calls, ran);

    /// <summary>Test seam for <see cref="WorkerBody"/>, which needs the sink's accumulator and so
    /// cannot be static.</summary>
    public string? WorkerBodyForTest(Job job) => WorkerBody(job);

    /// <summary>
    /// A finished WORKER's body: what it DID, as a table of its tool calls.
    ///
    /// <para>Returns null for every other row, which is what leaves a shell job's stdout and a
    /// tool's error message exactly as they were — this replaces a narration, not a result.</para>
    ///
    /// <para>THE PROSE IS STILL ON THE JOB. It is not rendered here, but <c>Output["content"]</c> is
    /// untouched and is what IntrospectionTools reads, which is how the ORCHESTRATOR consumes a
    /// worker's answer. A later opt-in — a second expand level, or a <c>/worker &lt;id&gt;</c>
    /// command — needs no new plumbing to surface it, only somewhere to put it.</para>
    ///
    /// <para>WHY THE PROSE IS NOT THE BODY. It is the transcript the worker narrated, and on a long
    /// run it is enormous: expanding a finished row put a wall of text on screen at the moment the
    /// user wanted to know what happened. The calls answer that question in the space the narration
    /// spent introducing itself, and the row is collapsed until someone asks — so a long table costs
    /// nothing that the unbounded prose it replaces did not cost more of.</para>
    /// </summary>
    private string? WorkerBody(Job job)
    {
        if (job.JobType != "llm_agent") return null;

        // THE CHILD GOES FIRST, BEFORE ANY OF THE RETURNS BELOW. Every path through this method is a
        // TERMINAL transition, so the live body will never be drawn for this row again — and the
        // failure paths are the ones a leak would collect, since a spawn that broke before answering
        // has no envelope for the branch below to key on. Keyed by the parent's job id, which every
        // one of those paths still has.
        _workerChildren.TryRemove(job.Id, out _);

        // THE ENVELOPE IS THE JOIN. A spawn's row is the PARENT's job, and its child's calls are
        // filed under the CHILD's agent id — which the envelope carries and nothing else on this
        // job does.
        if (SubAgentEnvelope.IdOf(RawContent(job)) is not { } childId) return null;

        // A WORKER THAT DID NOT SUCCEED KEEPS ITS REASON, and this is the one case where the prose
        // is not a narration. BodyText's failure branch composes the error and whatever partial
        // output there was, and that is what the user's next action depends on — a timetable of the
        // calls that ran before it broke answers a question nobody is asking yet.
        //
        // THE ENVELOPE'S NOTE IS PART OF WHAT SURVIVES BY RETURNING NULL HERE. A capped or stuck run
        // carries "hit its turn limit… NOT a completed answer" between the tag and the text, and
        // StripEnvelope keeps it deliberately. A table in its place would drop the one line saying
        // the answer above it is unfinished.
        //
        // THE CALLS ARE STILL DROPPED. The body is one question and the accumulator is another: a
        // failed child's calls are no less finished for its having failed, and forgetting them here
        // leaks exactly the runs a long session has most of.
        if (job.State != JobState.Succeeded)
        {
            _workerCalls.TryRemove(childId, out _);
            return null;
        }

        // REMOVED, NOT READ. This is the terminal transition, so nothing more will be filed under
        // this id; leaving it would grow a dictionary of every call the session ever made.
        var calls = _workerCalls.TryRemove(childId, out var recorded) ? recorded : [];

        lock (calls) return Timetable(calls.ToList(), job.Result?.Duration ?? TimeSpan.Zero);
    }

    /// <summary>
    /// The table itself: a summary line, then every call in the order it ran.
    ///
    /// <para>NO TRUNCATION, and that is a decision rather than an omission. The row is collapsed
    /// until someone opens it, and there is no second level to expand into — so a "… and 34 more"
    /// line would put the rest of the run nowhere at all. A long table is strictly better than the
    /// unbounded narration it replaces.</para>
    ///
    /// <para>A WORKER THAT CALLED NOTHING GETS THE SUMMARY LINE ALONE. An empty table under a header
    /// row says only that the <c>expand…</c> lied; "0 calls · 1.2s" is a fact about the run.</para>
    /// </summary>
    /// <param name="calls">Every call that has FINISHED, in the order it ran.</param>
    /// <param name="ran">How long the run has lasted — its final duration, or its elapsed time so far.</param>
    /// <param name="live">
    /// How the run is going right now, or null for one that is over.
    /// </param>
    private static string Timetable(IReadOnlyList<ToolCallReport> calls, TimeSpan ran,
        LiveRun? live = null)
    {
        // EVERY NUMBER THROUGH DisplayNumber, and the duration especially. A bare ":0.0" takes the
        // CURRENT CULTURE's decimal separator, so the same run reads "6.2s" or "6,2s" depending on
        // the machine — and a comma reads as a GROUP separator to a reader expecting the other.
        var parts = new List<string>
        {
            $"{DisplayNumber.Grouped(calls.Count)} call{(calls.Count == 1 ? "" : "s")}",
        };

        if (calls.Count > 0)
        {
            var tools = calls.Select(c => c.ToolName).Distinct(StringComparer.Ordinal).Count();
            parts.Add($"{DisplayNumber.Grouped(tools)} tool{(tools == 1 ? "" : "s")}");
        }

        parts.Add($"{DisplayNumber.Fixed(ran.TotalSeconds, 1)}s");

        // ZERO COUNTS ARE OMITTED. "16 calls · 4 tools · 6.2s · 0 failed · 0 denied" makes the
        // reader check two words before discarding them, on every row that went fine — and the
        // whole point of the line is that a bad run announces itself.
        AddCount(parts, calls, "failed", "failed");
        AddCount(parts, calls, "denied", "denied");
        AddCount(parts, calls, "cancelled", "cancelled");

        var summary = string.Join(" · ", parts);
        if (calls.Count == 0 && live?.InFlight is null) return summary;

        var rows = new List<string>
        {
            summary,
            "",
            live is null ? "|   | tool | target | ms | out |" : "|   | tool | target | ms |",
            live is null ? "|---|------|--------|---:|----:|" : "|---|------|--------|---:|",
        };

        foreach (var call in calls)
        {
            // Md.CodeSpan INSIDE THE SPAN, Md.EscapeCell OUTSIDE IT. A span processes no escapes at
            // all, so a backslash there SHOWS rather than protects — and tool names are almost all
            // underscored (read_file, run_shell, write_file), which would put a stray backslash on
            // nearly every row. A bare cell has no such shelter: a raw pipe splits the row into more
            // cells than the header declares and Markdig drops the overflow silently.
            var tool = Md.CodeSpan(call.ToolName);
            var target = string.IsNullOrWhiteSpace(call.Target) ? "" : Md.EscapeCell(call.Target!);

            var outCell = live is null ? $" {Bytes(call.ResultChars)} |" : "";
            rows.Add($"| {Mark(call.Outcome)} | {tool} | {target} "
                   + $"| {DisplayNumber.Grouped(call.DurationMs)} |{outCell}");
        }

        if (live?.InFlight is { } running) rows.Add(InFlightRow(running));

        return string.Join("\n", rows);
    }

    /// <summary>
    /// The last row of a live table: what the child is doing right now, spinning, with its own clock.
    ///
    /// <para>A GLYPH DERIVED FROM ELAPSED MONOTONIC TIME rather than an inline <c>[spinner]</c> tag.
    /// The header can carry that tag because a header is markup the parser resolves at paint time;
    /// this is a MARKDOWN table cell, where the tag would render as its own literal text. The same
    /// derivation the framework's own clock does, at the same cadence, so the two animate together.
    /// </para>
    ///
    /// <para>WHAT TICKS IT IS <see cref="RefreshRunningHeadersNow"/>, exactly as with the header's
    /// clock and for exactly its reason: this is a pure projection of the job and the moment it was
    /// called, so nothing re-derives it unless something re-renders the body.</para>
    ///
    /// <para>THE SAME COLUMNS AS A FINISHED ROW, minus <c>out</c> — which the whole table is missing
    /// while it is live. A row that changed shape when it settled would be the one thing this design
    /// is trying not to do.</para>
    /// </summary>
    private static string InFlightRow(Job job)
    {
        // THE SAME TWO COLUMNS AS A FINISHED ROW, split out of what the child's panel holds.
        // PlanLocalId IS the tool name on a call row — Agent.InvokeAndShowAsync sets it to call.Name
        // — and DisplayName is that name followed by the arguments, so dropping the leading token
        // leaves the target. Rendering the whole label in one column instead put `run_shell dotnet
        // build` under `target` with the tool column blank, which is the row changing shape when it
        // settles: the exact thing this design exists not to do.
        var tool = job.PlanLocalId is { Length: > 0 } named ? named : job.DisplayName;

        // Md.CodeSpan INSIDE, Md.EscapeCell OUTSIDE — the same split the finished rows above use, and
        // for the same reason: a span processes no escapes, so EscapeCell's backslash would SHOW on
        // nearly every row (read_file, run_shell, write_file are all underscored), while a bare cell
        // has no shelter from a raw pipe splitting the row.
        var target = job.DisplayName.Length > tool.Length && job.DisplayName.StartsWith(tool, StringComparison.Ordinal)
            ? Md.EscapeCell(job.DisplayName[tool.Length..].Trim())
            : "";

        // MILLISECONDS, in the same column and the same format as every finished row above, so the
        // in-flight call's cost is compared against theirs directly. Through DisplayNumber like all
        // the rest: a bare format takes the current culture's group separator.
        var ms = DisplayNumber.Grouped((long)Elapsed(job).TotalMilliseconds);

        return $"| {SpinnerGlyph()} | {Md.CodeSpan(tool)} | {target} | {ms} |";
    }

    /// <summary>
    /// The Braille frame for right now — the same alphabet the header's inline spinner animates
    /// through, so a live row and its header do not spin in two different scripts.
    /// </summary>
    private static string SpinnerGlyph()
    {
        const string frames = "⣷⣯⣟⡿⢿⣻⣽⣾";

        // MONOTONIC, not wall-clock: Environment.TickCount64 cannot step backwards over an NTP
        // correction or a daylight-saving change, either of which would make the glyph jump or stall.
        var frame = (int)(Environment.TickCount64 / SpinnerIntervalMs % frames.Length);
        return frames[frame].ToString();
    }

    /// <summary>Appends "<c>2 denied</c>" when there are any, and nothing when there are none.</summary>
    private static void AddCount(List<string> parts, IReadOnlyList<ToolCallReport> calls,
        string outcome, string word)
    {
        var n = calls.Count(c => string.Equals(c.Outcome, outcome, StringComparison.Ordinal));
        if (n > 0) parts.Add($"{DisplayNumber.Grouped(n)} {word}");
    }

    /// <summary>
    /// The outcome glyph, and FOUR of them rather than two.
    ///
    /// <para>✗ ran and broke; ⊘ never ran, because permission was refused; – never finished, because
    /// the user stopped it. Those are three different problems with three different fixes, and a
    /// column that renders them all as "failed" tells a reader to go and debug the wrong one.</para>
    /// </summary>
    private static string Mark(string outcome) => outcome switch
    {
        "succeeded" => "✓",
        "denied" => "⊘",
        "cancelled" => "–",
        _ => "✗",
    };

    /// <summary>
    /// How much the result put into the child's context, at the scale a reader compares by.
    ///
    /// <para>CHARACTERS COUNTED AS BYTES, which the column header says by naming kB. ResultChars is
    /// what the loop measures — the figure that says whether a tool is cheap or is quietly filling
    /// the window — and re-encoding it here to be exact about multi-byte runes would spend real work
    /// on a digit nobody reads at this scale.</para>
    /// </summary>
    private static string Bytes(int chars) =>
        chars <= 0 ? "0"
        : chars < 1024 ? $"{DisplayNumber.Grouped(chars)} B"
        : chars < 1024 * 1024 ? $"{DisplayNumber.Fixed(chars / 1024.0, 1)} kB"
        : $"{DisplayNumber.Fixed(chars / (1024.0 * 1024.0), 1)} MB";

}
