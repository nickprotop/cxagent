using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.UI;

/// <summary>
/// P11 Task 3: the point of the plan. Truncation (<see cref="SessionCommands.Compress"/>) deleted four
/// goals' worth of findings outright on a live drive — nothing condensed, everything discarded. This
/// asks the model to condense the OLDEST half into one factual message and keeps the newest half
/// VERBATIM, so a follow-up ("now do X to that") still has its referent.
///
/// One occasional summarising call is far cheaper than re-sending full history on EVERY subsequent
/// call — a live session reached 35,523 tokens, most of it re-sent context. It also helps prompt
/// caching: replacing a long prefix with a short STABLE one beats front-truncation, which invalidates
/// every cached token exactly when the conversation is largest.
/// </summary>
public static class SessionCompressor
{
    /// <summary>What actually happened. <see cref="Summarised"/> is false when the provider call
    /// failed and truncation ran instead — the caller (AgentHost) uses this to say so, because a
    /// silent degradation to today's behaviour is worse than an honest one.</summary>
    /// <param name="Summarised">
    /// True when the work was done properly — either stale tool output was cleared or the model wrote
    /// a summary. False means the summarising call failed and the oldest messages were dropped
    /// unread, which the caller shows in red: a silent degradation to truncation is worse than an
    /// honest one.
    /// </param>
    /// <param name="CharsFreed">Characters reclaimed, for the row and the status bar.</param>
    /// <param name="Summary">The summary the model wrote, when it answered.</param>
    /// <param name="Declined">
    /// True when the compression chose NOT to run and nothing was lost — the model returned no usable
    /// summary, so the conversation was left intact.
    ///
    /// <para>Distinct from the other <c>Summarised: false</c> case, where summarisation THREW and the
    /// oldest messages were dropped unread. Both are "not summarised"; only one costs history, and a
    /// row that reports them identically tells the user they lost something they did not.</para>
    /// </param>
    public readonly record struct CompressResult(
        bool Summarised, int CharsFreed = 0, string? Summary = null, bool Declined = false);

    /// <summary>
    /// Where to split, so that summarising the head can never leave the tail holding a tool result
    /// whose call has been summarised away.
    ///
    /// <para>THE MIDPOINT ALONE IS NOT SAFE. <c>ToolCallId</c> is the only thing binding a tool result
    /// to the assistant message that called for it, and providers reject a result whose call is
    /// absent. A blind <c>Count / 2</c> cut lands mid-pair on any conversation with an even number of
    /// tool pairs — simulated across list shapes, it orphaned a result in four of eight cases. That
    /// hazard is why the single-agent loop discarded its whole working context at the end of every
    /// goal rather than keeping tool messages at all; removing the hazard removes the reason.</para>
    ///
    /// <para>The fix is to walk the boundary back while the tail would begin on a tool result. At most
    /// one step is ever needed — a result is always immediately preceded by its call — but the loop is
    /// written as a loop rather than a single test so that a future provider allowing several results
    /// per call does not silently reintroduce the bug. Aider arrives at the same rule from the same
    /// pressure, walking its split back until the head ends on an assistant message.</para>
    /// </summary>
    /// <remarks>Public rather than internal: this codebase has no InternalsVisibleTo grant, and the
    /// orphan hazard is exactly the kind of thing that must stay covered by a test.</remarks>
    public static int SafeCut(IReadOnlyList<ChatMessage> conversation)
    {
        var cut = conversation.Count - conversation.Count / 2;
        while (cut > 0 && conversation[cut].ToolCallId is not null) cut--;
        return cut;
    }

    /// <summary>
    /// Splits the conversation in two at the same midpoint <see cref="SessionCommands.Compress"/>
    /// uses (oldest half / newest half), asks <paramref name="provider"/> to condense the oldest half
    /// into one factual message, and replaces that half with it. The newest half is left byte-for-byte
    /// alone.
    ///
    /// Falls back to <see cref="SessionCommands.Compress"/> — unchanged, the same routine `/compress`
    /// calls — when the summarising call throws, so `/compress` and auto-compression share one
    /// degradation path. A housekeeping failure must not kill a working session.
    /// </summary>
    /// <remarks>
    /// TAKES THE CONTEXT, NOT A LIST. Passing the bare <c>List&lt;ChatMessage&gt;</c> is how the
    /// worst bug in this area happened: <c>/compress</c> was handed the session conversation while the
    /// list that was actually full sat elsewhere, so it reported "nothing to free" on a session
    /// measured at 58,000 tokens. A list detached from its owner can always be the wrong list. An
    /// <see cref="AgentContext"/> cannot — there is exactly one per agent, and compressing it is by
    /// definition compressing that agent's context.
    ///
    /// <para>It also lets this method invalidate the occupancy reading itself, which is the truth it
    /// is in the best position to know: after rewriting the conversation, the last measurement no
    /// longer describes it.</para>
    /// </remarks>
    public static async Task<CompressResult> CompressAsync(
        AgentContext context, ILlmProvider provider, CancellationToken ct,
        Action<LlmUsage>? meter = null)
    {
        var conversation = context.Messages;

        // NO MESSAGE-COUNT FLOOR — see SessionCommands.Compress for why. This ran only on an explicit
        // /compress or on measured TOKEN pressure, and neither is answered by counting messages: eight
        // messages carrying four large file reads is precisely the case that needs compressing, and
        // the old floor of eight declined it silently.
        //
        // Two is arithmetic, not policy: below that there is no older half to summarise.
        if (conversation.Count < 2)
            return new CompressResult(Summarised: false);

        // SUMMARISING IS THE ONLY TIER, deliberately.
        //
        // A cheaper one lived here: it emptied tool results whose file had since been read or written
        // again, on the reasoning that a fresher copy of the same thing existed further down. It
        // worked — measured live at −18% reclaimed instantly, against −24% for a ~25-second provider
        // call — and it was removed anyway, because the evidence for it is thinner than the
        // measurement suggests.
        //
        // Only Cline ever shipped content deduplication, and it is GONE from Cline's HEAD; the repo
        // moved to compaction-based management. opencode has a pruner and ships it OFF by default
        // ("Enable pruning of old tool outputs (default: false)"). Claude Code and Antigravity prune
        // aggressively but PERSIST TO DISK FIRST, so nothing they drop is unrecoverable — an option
        // not open to us, which is what made our rule have to be conservative in the first place.
        //
        // And the conservative rule had a hole. It keyed on the file PATH, but read_file takes offset
        // and limit: reading lines 1-40 and later lines 200-240 are not copies of one another, and
        // treating the second as superseding the first would have discarded content nothing else
        // held. Fixable — but fixing an unsound optimisation nobody else ships, to save a provider
        // call, is the wrong order of work.
        //
        // Summarisation READS what it discards and can carry a detail forward. That is the property
        // worth having while the shape of real usage is still unknown. If sessions turn out to be
        // dominated by re-read file content, this is the first thing to revisit — with persistence
        // underneath it, or as Claude Code's forward-looking suppression, which declines the
        // redundant read instead of erasing the old one.
        // BELOW THE PINNED HEAD. The system preamble leads the conversation and is not history:
        // summarising it away deletes the instructions that keep the model on real paths, and the
        // agent then re-inserts it above the summary on the next turn. Everything here is offset by
        // the pin — SafeCut itself is untouched, and deliberately so: its tool-pair walk-back is
        // verified sound and the orphan hazard it prevents has nothing to do with the head.
        var pin = context.PinnedHeadCount;
        var cut = SafeCut(conversation);
        if (cut <= pin) return new CompressResult(Summarised: false);
        var oldTurns = conversation.GetRange(pin, cut - pin);

        try
        {
            var before = context.TotalChars();
            var summary = await SummariseAsync(oldTurns, provider, ct);

            // METERED REGARDLESS. The call was made and billed whether or not its answer is usable;
            // not recording it would hide spend from the ledger.
            meter?.Invoke(summary.Usage);

            // NOTHING USEFUL CAME BACK — so nothing is discarded. The call SUCCEEDED (a thrown error
            // takes the truncation path below), it simply said nothing worth keeping, and splicing it
            // in would delete half the conversation and leave "" as the only record of it. Declining
            // is the conservative choice: the context stays over its threshold and the next turn
            // tries again, which is strictly better than a session whose memory is a blank.
            if (!IsUsableSummary(summary.Text))
                return new CompressResult(Summarised: false, Declined: true);

            conversation.RemoveRange(pin, cut - pin);
            conversation.Insert(pin, new ChatMessage
            {
                Role = "assistant",
                Content = FormatSummary(summary.Text),
                Timestamp = DateTimeOffset.UtcNow,
            });
            // THE READING NO LONGER DESCRIBES THE CONVERSATION. Done here rather than left to the
            // caller because this method is what invalidated it, and a caller that forgot would leave
            // the status bar confidently reporting a number for a context that no longer exists.
            context.EstimateUsageAfterCompaction();

            return new CompressResult(Summarised: true,
                CharsFreed: Math.Max(0, before - context.TotalChars()),
                Summary: summary.Text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Summarisation failed — fall back to the SAME truncation /compress uses, never a
            // second, divergent degradation path.
            var beforeTruncate = context.TotalChars();
            SessionCommands.Compress(conversation, pin);
            context.EstimateUsageAfterCompaction();
            return new CompressResult(Summarised: false,
                CharsFreed: Math.Max(0, beforeTruncate - context.TotalChars()));
        }
    }


    /// <summary>
    /// The summarising call itself: a plain ChatAsync with no tools — there is nothing to decide, only
    /// text to condense. Uses the SAME provider the goal is using; no separate "summarising model"
    /// config knob (a real question, but a separate one — an unused knob is worse than none).
    /// </summary>
    private static Task<LlmResponse> SummariseAsync(
        List<ChatMessage> oldTurns, ILlmProvider provider, CancellationToken ct)
    {
        // RENDER THE TOOL CALLS, NOT JUST Content. An assistant message that makes a tool call
        // carries EMPTY Content — the call is in ToolCalls — so joining on Content alone handed the
        // model blank lines exactly where the work was. Measured live: two compactions in one session
        // produced 100-character "summaries" that were a bare tool name and nothing else, because
        // that was all the transcript contained. The file contents were in the tool RESULTS, which
        // rendered fine; what vanished was every trace of what had been asked and decided.
        var transcript = string.Join("\n", oldTurns.Select(Render));

        var request = new List<ChatMessage>
        {
            new()
            {
                Role = "user",
                Content =
                    "You are compacting your own working memory. Everything below is about to be "
                    + "REPLACED by what you write, and you will keep working from it — so write what "
                    + "you would need to continue, not what you would tell someone about the past.\n\n"
                    + "Record, when present:\n"
                    + "- Files read or changed, by exact path, and what each contains or now contains.\n"
                    + "- What was established as TRUE: findings, causes, measurements, decisions.\n"
                    + "- Commands run and their outcome — especially builds and tests, with the exact "
                    + "pass/fail counts and any error text still unresolved.\n"
                    + "- Anything asked for and NOT yet done, and anything tried that did not work, "
                    + "so it is not attempted again.\n\n"
                    + "Keep concrete details verbatim — paths, identifiers, counts, values, error "
                    + "messages. A vague summary is worse than a short one: \"fixed the parser\" is "
                    + "useless where \"IndentShift.cs Refuse() now requires a 2-of-3 majority\" is not. "
                    + "No narrative, no commentary, no pleasantries, and do not describe the "
                    + "conversation itself. Facts only.\n\n"
                    + "If some part of this history is uncertain or was never resolved, say so "
                    + "plainly rather than inventing a resolution.\n\n"
                    + "Reply with the note itself and nothing else. Do not call any tool — the "
                    + "history below DESCRIBES tools that were already used; it is not a request "
                    + "to use them again.\n\n"
                    + transcript,
            },
        };

        return provider.ChatAsync(request, tools: null, ct);
    }

    /// <remarks>Test seam: this codebase has no InternalsVisibleTo grant, and the two defects this
    /// rendering fixes were both invisible from unit tests until a live drive exposed them.</remarks>
    public static string RenderForTest(ChatMessage m) => Render(m);

    /// <summary>
    /// One message as a line of transcript, with tool calls and results made visible.
    /// </summary>
    /// <remarks>
    /// A tool RESULT is labelled as one rather than left as a bare "tool:" line, so the model can tell
    /// the difference between what it asked for and what came back. Results are capped: the point of
    /// summarising is to condense file contents, and re-sending a 35,000-character read in full to be
    /// told about it costs the whole saving. The head is kept because a read's opening lines identify
    /// the file.
    /// </remarks>
    private static string Render(ChatMessage m)
    {
        if (m.ToolCallId is not null)
        {
            var body = m.Content ?? "";
            var clipped = body.Length <= ToolResultChars
                ? body
                : body[..ToolResultChars] + $"\n… [{body.Length - ToolResultChars:N0} more characters]";
            return $"tool result: {clipped}";
        }

        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(m.Content)) parts.Add(m.Content);
        if (m.ToolCalls is { Count: > 0 })
            foreach (var c in m.ToolCalls)
                // NAME AND TARGET ONLY, never the raw arguments. Rendering the full call gave the
                // model something that looked like a call to MAKE: it answered a summarise request by
                // emitting a tool-call block, which became the "summary". A description cannot be
                // mistaken for an instruction.
                parts.Add($"(used {c.Name}{Target(c)})");

        return $"{m.Role}: {string.Join(" ", parts)}";
    }

    /// <summary>The path a call names, when it names one — enough to say WHICH file without handing
    /// back a callable argument blob.</summary>
    private static string Target(ToolCall call)
    {
        if (call.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
        if (!call.Arguments.TryGetProperty("path", out var p)) return "";
        return p.ValueKind == System.Text.Json.JsonValueKind.String ? $" on {p.GetString()}" : "";
    }

    /// <summary>How much of a tool result the summariser is shown. Enough to identify the file and
    /// its shape; not so much that summarising costs what it saves.</summary>
    private const int ToolResultChars = 2_000;

    /// <summary>
    /// Whether a summary is worth trading half the conversation for.
    ///
    /// <para>Three ways it is not. Empty or whitespace is the obvious one. Too short is the next: a
    /// model answering "ok" or "done" has acknowledged the request rather than performed it, and that
    /// word would become the session's whole memory of what it replaced. A refusal is the third — it
    /// is a coherent sentence and passes any length test, but it describes the model declining, not
    /// the history it was asked about.</para>
    ///
    /// <para>Deliberately narrow. The cost of wrongly REJECTING is one skipped compaction: the
    /// context stays large and the next turn tries again. The cost of wrongly ACCEPTING is history
    /// deleted and replaced with nothing. Those are not symmetric, so this errs toward rejecting —
    /// but only on signals that cannot be a real summary, never on a terse one.</para>
    ///
    /// <para>A vacuous-but-coherent reply ("Summary of the above.") still passes. No length rule can
    /// catch that without also rejecting "read Foo.cs, changed the parser", and given the asymmetry
    /// above, letting it through is the right side to err on.</para>
    /// </summary>
    private static bool IsUsableSummary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.Length < MinSummaryChars) return false;

        foreach (var opener in RefusalOpeners)
            if (trimmed.StartsWith(opener, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    /// <summary>
    /// The shortest thing that can be a real summary. Long enough to exclude "ok", "done", "none",
    /// "summary", "Summary:" and "No summary." — acknowledgements of the request, or declarations of
    /// having nothing to say, rather than answers to it.
    ///
    /// <para>TWELVE, AND IT CAME DOWN TWICE. Forty rejected "read Foo.cs, changed the parser" (31);
    /// twenty then rejected "earlier: a summary." (19). Both are real summaries of short exchanges,
    /// and both were caught by existing tests rather than by reasoning — which is the point: this
    /// floor exists to catch a model that said NOTHING, not to impose a word count on one that was
    /// brief because the history it summarised was.</para>
    /// </summary>
    private const int MinSummaryChars = 12;

    /// <summary>How a refusal starts. Matched at the FRONT only: a real summary may well contain the
    /// words "cannot" or "sorry" while describing what happened, and rejecting on that would throw
    /// away good summaries of sessions that went badly.</summary>
    private static readonly string[] RefusalOpeners =
    [
        "I cannot", "I can't", "I'm sorry", "I am sorry", "I'm unable", "I am unable",
        "Sorry,", "As an AI",
    ];

    private static string FormatSummary(string? text) =>
        $"[earlier conversation, summarised: {text}]";
}
