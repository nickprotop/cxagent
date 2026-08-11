using System.Collections.Concurrent;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;

using CxAgent.Core.Agent;
// Ours, not SharpConsoleUI's — both exist and this file sees both namespaces.
using ChatMessageId = CxAgent.Core.Agent.ChatMessageId;

namespace CxAgent.UI;

/// <summary>
/// An <see cref="IJobPanel"/> that writes jobs INTO the conversation instead of a side panel — the
/// Claude Code / opencode shape, one column, jobs interleaved with the turns that caused them.
///
/// <para>Each job becomes one transcript message whose STATUS row is rewritten in place as the job
/// moves Pending → Running → Succeeded/Failed. <c>ChatTranscriptControl.SetStatus(id, text, severity)</c>
/// is keyed by message id, so a job's line updates where it already sits rather than appending a new
/// line per transition — which would bury the conversation under status spam on a seven-job goal.</para>
///
/// <para>This exists at all because <see cref="IJobPanel"/> was already the seam between AgentHost
/// and the UI: the engine calls SetJobs/UpdateJob and never touches a control. Swapping the layout is
/// therefore a UI-only change — no engine edits, and the copilot draft gate keeps working unchanged
/// because it too speaks through this interface.</para>
/// </summary>
public sealed class InlineJobSink : IJobPanel
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
    public void SetJobs(IReadOnlyList<Job> jobs) =>
        _system.EnqueueOnUIThread(() =>
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
                // UpdateJob already does this; SetJobs did not, so every row showed it until its
                // first transition.
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
                _chat.SetStatus(id, StatusText(job), SeverityFor(job.State));
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
            // nothing to expand into. Gating this on IsTerminal left a live tool rendering the full
            // block with an "expand…" affordance revealing an empty body, which is exactly the noise
            // this was meant to remove, and it is on screen for the whole time the tool runs.
            _chat.SetCompactFooter(id, IsCompactRow(job) || !IsTerminal(job.State));
            }
        });

    public void UpdateJob(Job job) =>
        _system.EnqueueOnUIThread(() =>
        {
            _known[job.Id] = job;

            // A hidden job gains a row only if it later FAILS — ShouldShow flips then, and the
            // adopt-on-first-update path below creates the line it never had.
            if (!ShouldShow(job)) return;

            // A job the orchestrator added mid-goal can transition before any SetJobs mentions it —
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
                // SetJobs, and for a long time only that one set the header -- so a STREAMING
                // worker, which AppendText switches out of compact mode on its first delta, kept
                // the pending spinner it was born with for the rest of the session. SetJobs is the
                // add path and rarely fires on completion; UpdateJob is where a job actually
                // finishes, so the row that most needed the header was the one never getting it.
                // Seen live: a worker showing a spinning Braille frame beside its own finished
                // review body, with "Goal completed." printed below it.
                _chat.SetHeader(id, CompactHeader(job));
                _chat.SetStatus(id, StatusText(job), SeverityFor(job.State));
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
            // nothing to expand into. Gating this on IsTerminal left a live tool rendering the full
            // block with an "expand…" affordance revealing an empty body, which is exactly the noise
            // this was meant to remove, and it is on screen for the whole time the tool runs.
            _chat.SetCompactFooter(id, compactRow);

            // EXPAND a compact row. SetCompactFooter removes the separator rule and the trailing
            // blank, but the "expand…" affordance is a DIFFERENT mechanism — the Tool role sets
            // StartCollapsed = true, so every row opens collapsed regardless of what its footer
            // does. A compact row has nothing behind that affordance (its whole content is the one
            // line), so leaving it collapsed offers to expand into a copy of the header, which is
            // exactly the noise being removed. Seen live: "▸ Tool │   expand… │ running…".
            if (compactRow)
                _chat.SetExpanded(id, true);

            // THE BODY. Until now this method only touched the status row, so a job's message stayed
            // whatever Title() produced when it first appeared — its NAME. The model's actual output
            // was written by every plugin (LlmAgentJobPlugin's transcript, ShellJobPlugin's stdout,
            // both into Output["content"]) and read by NOTHING except IntrospectionTools, which is how
            // the ORCHESTRATOR reads results. The user could not see what their own workers said.
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
            if (!IsTerminal(job.State))
                _chat.UpdateMessage(id, string.Empty);

            // A COMPACT row folds its whole result into ONE line and drops the expand affordance:
            // "20" does not need a header, a body, a full-width rule, a status row and a blank line
            // to be read. The header carries the name, the ⎿ carries the result.
            if (IsTerminal(job.State) && IsCompactRow(job))
            {
                // THE WHOLE OUTPUT, in the body, COLLAPSED. The row used to fold to its one-line
                // summary and throw the body away, so "⎿ 254 lines, 8,990 chars" was all you could
                // ever see — the actual text was only in the log file. Now the summary line stays as
                // the collapsed header and the full output lives one keypress behind it.
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
                _chat.UpdateMessage(id, StripEnvelope(BodyFor(job)) ?? string.Empty);

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
                // FAILURE NO LONGER EXPANDS. It did, on the reasoning that the error and the buttons
                // that act on it must be visible together — but those inline buttons were removed
                // (see below), and what is left is an ordinary failed tool call. A read_file that
                // missed is a one-line fact, and expanding it printed the same error twice, header
                // and body, on every miss. The header already says `failed`, and `expand…` is there
                // for anyone who wants the detail.
                // EVERYTHING COLLAPSES NOW, INCLUDING A SPAWN.
                //
                // The rule was "a worker stays expanded", written when llm_agent meant a STREAMING
                // worker: its prose had been visible and growing for the whole run, so collapsing at
                // the finish line snatched away what the user was reading. That argument does not
                // survive contact with a sub-agent, which is the opposite case — its work is
                // BUFFERED and was never on screen (D22), so there is nothing to snatch. What lands
                // at the finish line is a wall of new text the user did not ask to have opened.
                //
                // Seen in a screenshot: a completed spawn opened its whole envelope into the
                // transcript, pushing the parent's own answer — the thing the user was waiting for —
                // down the screen behind it. The answer is the parent's reply; the child's report is
                // the working, and working belongs one keypress away.
                _chat.SetExpanded(id, false);
            }

            // NO INLINE BUTTONS. Retry/Skip/Diagnose were removed: they invited the user to drive
            // the scheduler by hand while the orchestrator was mid-drive, which produced "a drive
            // operation is already in progress; drive operations must not overlap" on screen, and a
            // hand-skipped job desynchronised the plan from what the orchestrator believed had run.
            // The failure message and its reason stay -- they are what the model reads on the next
            // consult, and letting it re-plan is both more reliable and the whole point of a loop
            // that already has a repair round.
            _chat.ClearActions(id);

            // REFRESH THE DEPENDENTS. UpdateJob fires only for the job that transitioned, so a job
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
        var output = job.Result?.Output;

        // A failed job's REASON is the body — the user's next action depends on it, and the status
        // row truncates a stack trace or multi-line stderr into unreadability.
        if (job.State == JobState.Failed)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(job.Result?.ErrorMessage))
                parts.Add(job.Result!.ErrorMessage!);

            // STDERR, which is the whole point. A failed shell job's ErrorMessage is literally
            // "command exited with code 1" (ShellJobPlugin.cs:49) — the actual reason lives in
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
            && job.PluginType == "shell"
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
    /// UNCLIPPED. The body used to cap at 4,000 chars with a visible marker, on the reasoning that a
    /// long transcript makes the conversation unnavigable — but the row is COLLAPSED by default now,
    /// so length costs nothing until the user opens it, and a clipped body meant the full text was
    /// only ever in the log file. Kept as a pass-through rather than deleted so the call sites and
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
    /// THE HEADER, AND NOTHING ELSE — the whole point of not routing a tick through UpdateJob, which
    /// calls SetExpanded(id, true) and UpdateMessage(id, "") on every invocation.
    ///
    /// <para>Silently ignores a job with no row yet: a progress tick is not the add path, and adopting
    /// an unknown job here would race SetJobs into drawing the row twice.</para>
    /// </summary>
    public void UpdateProgress(Job job) =>
        _system.EnqueueOnUIThread(() =>
        {
            _known[job.Id] = job;
            if (_lines.TryGetValue(job.Id, out var id))
                _chat.SetHeader(id, CompactHeader(job));
        });

    public void UpdateResources(string jobId, ResourceSnapshot snapshot) { }

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
    public void AppendText(string jobId, string delta) =>
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
    /// plugins. Based on the plugin TYPE and nothing else, because the author is fixed when the
    /// message is CREATED — before the job has run — so it cannot depend on what the job later
    /// produced. Verified: ChatTranscriptControl exposes no SetAuthor.</para>
    /// </summary>
    /// <summary>Test seam: the header label is a pure projection of the job.</summary>

    /// <summary>Every job belongs in the transcript. A succeeded PLANNER used to be hidden here;
    /// roles are gone, so there is no planner to hide.</summary>
    private static bool ShouldShow(Job job) => true;

    public static string AuthorForTest(Job job) => AuthorFor(job);

    private static string AuthorFor(Job job)
    {
        // Compression is not a tool the model called — it is housekeeping the app did to its own
        // context, and labelling it "Tool" would put it among the model's actions as though it had
        // chosen to run it.
        if (job.PluginType == "compress") return "Context";

        if (job.PluginType != "llm_agent") return "Tool";

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
    /// The header line for a compact step: its name plus the state that used to need a whole status
    /// row of its own — and, while running, an inline <c>[spinner]</c> so the row does not change
    /// SHAPE when it finishes, it just stops spinning.
    ///
    /// <para>This is what takes a tool step from three lines to two. The status row is the third
    /// line, and everything on it ("done · 0.0s") fits beside the name.</para>
    ///
    /// <para>Markup IS honoured here, unlike the message body: the header is not routed through the
    /// markdown pipeline, so a [spinner] tag renders as a spinner rather than as the literal text
    /// "[spinner]" — the trap that caught the ⎿ separator earlier.</para>
    /// </summary>
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
            // inheriting whatever else is on screen.
            return $"[spinner braille {SpinnerIntervalMs}] {author}  {name}{progress}";
        }

        var duration = job.Result?.Duration is { } d ? $" · {d.TotalSeconds:0.0}s" : "";
        var state = job.State switch
        {
            JobState.Succeeded => "done",
            JobState.Skipped => "skipped",
            JobState.Cancelled => "cancelled",
            _ => job.State.ToString().ToLowerInvariant(),
        };

        // A GLYPH where the spinner was, not nothing. Replacing the spinner with an empty string
        // shifts the whole row one cell left the instant a step finishes, so a column of steps
        // jitters as each completes. A static mark holds the column and reads as "settled".
        var mark = job.State == JobState.Succeeded ? "✔" : "•";

        // FINISHED WORK RECEDES. A completed row is history — it stays legible, and stops competing
        // for attention with the one row still running. Without this a screen of twenty finished
        // tool calls is twenty things all shouting equally, and the live one is lost among them.
        // opencode does exactly this (its completed rows drop to textMuted while active rows hold
        // theme.text) and it is the single mechanic that makes a long session readable.
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
                 + $"[{ColorScheme.DangerMarkup}]{state}{duration}[/]";

        return $"[{ColorScheme.MutedMarkup}]{mark} {author}  {name}  ·  {state}{duration}[/]";
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
            // size: it is prose the model composed, not an echo of something already on disk.
            if (job.PluginType == "llm_agent") return null;

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
            return $"⎿  {lineCount:N0} lines, {raw.Length:N0} chars";
        }

        // A SPAWN HAS NO RESULT LINE. Its body is the envelope — <sub_agent id=… state=…> and the
        // child's report — and none of that belongs on a one-line summary beneath the header.
        //
        // Seen in a screenshot: a finished spawn rendered its envelope's opening tag as the result
        // line, so the row read the id and `state="completed"` back at a user whose header already
        // said `done · 144.6s`. Two lines, one fact, and the noisier of the two was the machine's.
        //
        // KEYED ON THE ENVELOPE, NOT ON PluginType. llm_agent is the row TYPE and covers every
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
        string.IsNullOrWhiteSpace(job.DisplayName) ? job.PluginType : job.DisplayName;

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
    /// Every job the sink has been told about, by id — SetJobs/UpdateJob both record here. Needed
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
            .Select(d => string.IsNullOrWhiteSpace(d!.DisplayName) ? d.PluginType : d.DisplayName)
            .ToList();

        return blockers.Count == 0 ? null : string.Join(", ", blockers);
    }

    /// <summary>Milliseconds per spinner frame — Braille's own default; see the tag that uses it.</summary>
    private const int SpinnerIntervalMs = 100;

    private string StatusText(Job job)
    {
        var duration = job.Result?.Duration is { } d ? $" · {d.TotalSeconds:0.0}s" : "";

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
    /// <para>Driven by whether there is anything to READ, not by plugin type. The type rule this
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
    /// <para>FAILURE IS NO LONGER EXEMPT. It was — "a failed job keeps its error and its buttons
    /// whatever its size" — and the buttons are gone (see the NO INLINE BUTTONS note above), so the
    /// exemption was reserving a footer for an affordance that no longer exists. What it actually
    /// bought was four lines to say one thing: the header already ends in "failed · 0.0s", then the
    /// error, then a full-width rule, then "failed · 0.0s" AGAIN. Measured live on a missing file.
    ///
    /// <para>A failed row is now compact on the same terms as any other: the header carries the
    /// outcome and one line carries the reason. Nothing is hidden — the error is the body, and it
    /// is what the model reads on the next consult either way.</para>
    /// </summary>
    private static bool IsCompactRow(Job job)
    {
        if (OneLineRow(job) is not null) return true;

        // A tool's bulky output is an echo; a worker's is the answer.
        return job.PluginType != "llm_agent";
    }

    private static bool IsTerminal(JobState state) =>
        state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;
}
