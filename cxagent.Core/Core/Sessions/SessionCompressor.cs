using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Sessions;

/// <summary>
/// P11 Task 3: the point of the plan. Truncation (<see cref="Truncate"/>) deleted four
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
    /// <remarks>Public rather than internal: the grant in AssemblyWiring covers Session's assemble
    /// members, not this, and the
    /// orphan hazard is exactly the kind of thing that must stay covered by a test.</remarks>
    public static int SafeCut(IReadOnlyList<ChatMessage> conversation)
    {
        var cut = conversation.Count - conversation.Count / 2;
        while (cut > 0 && conversation[cut].ToolCallId is not null) cut--;
        return cut;
    }

    /// <summary>
    /// Splits the conversation in two at the same midpoint <see cref="Truncate"/>
    /// uses (oldest half / newest half), asks <paramref name="provider"/> to condense the oldest half
    /// into one factual message, and replaces that half with it. The newest half is left byte-for-byte
    /// alone.
    ///
    /// Falls back to <see cref="Truncate"/> — unchanged, the same routine `/compress`
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
    /// <param name="skillToolOffered">
    /// Whether the <c>skill</c> tool is available this session. False suppresses the "call skill
    /// again" remedy in the notice — telling a model to reload with a tool it does not have is the
    /// lie a selection must not create.
    /// </param>
    /// <param name="context">The conversation to compact, edited in place.</param>
    /// <param name="provider">The model that writes the summary — the SAME one the goal is using.</param>
    /// <param name="ct">Cancels the summarising call.</param>
    /// <param name="meter">Where the summary's own token cost is reported, or null to drop it.</param>
    public static async Task<CompressResult> CompressAsync(
        AgentContext context, ILlmProvider provider, CancellationToken ct,
        Action<LlmUsage>? meter = null, bool skillToolOffered = true)
    {
        var conversation = context.Messages;

        // NO MESSAGE-COUNT FLOOR — see Truncate for why. This runs only on an explicit
        // /compress or on measured TOKEN pressure, and neither is answered by counting messages: eight
        // messages carrying four large file reads is precisely the case that needs compressing, and
        // any floor in that range would decline it silently.
        //
        // Two is arithmetic, not policy: below that there is no older half to summarise.
        if (conversation.Count < 2)
            return new CompressResult(Summarised: false);

        // SUMMARISING IS THE ONLY TIER, deliberately.
        //
        // The cheaper tier on offer is content deduplication: empty the tool results whose file has
        // since been read or written again, on the reasoning that a fresher copy of the same thing
        // sits further down. It does reclaim — measured live at −18% instantly, against −24% for a
        // ~25-second provider call — and it is still not worth having here.
        //
        // Deduplication is not what the field settled on. Cline's HEAD has none; the repo runs on
        // compaction. opencode ships its pruner OFF by default ("Enable pruning of old tool outputs
        // (default: false)"). Claude Code and Antigravity prune aggressively but PERSIST TO DISK
        // FIRST, so nothing they drop is unrecoverable — an option not open to us, which is why any
        // rule here would have to be conservative.
        //
        // And the conservative rule has a hole. Keying on the file PATH is unsound because read_file
        // takes offset and limit: lines 1-40 and lines 200-240 are not copies of one another, and
        // treating the second as superseding the first discards content nothing else holds. Fixable
        // — but fixing an unsound optimisation nobody else ships, to save a provider call, is the
        // wrong order of work.
        //
        // Summarisation READS what it discards and can carry a detail forward. That is the property
        // worth having while the shape of real usage is still unknown. If sessions turn out to be
        // dominated by re-read file content, this is the first thing to revisit — with persistence
        // underneath it, or as Claude Code's forward-looking suppression, which declines the
        // redundant read instead of erasing the earlier one.
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

            // A PREVIOUS SUMMARY IS UPDATED, NOT RE-SUMMARISED. Without this the second compaction
            // reads only the first one's prose, and detail decays geometrically across a long
            // session — which is exactly why late compactions become useless. Pulled out of the head
            // so it is merged rather than condensed again.
            var previousSummary = ExtractPreviousSummary(oldTurns);
            var summary = await SummariseAsync(oldTurns, provider, ct, previousSummary);

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
                Content = FormatSummary(summary.Text) + SkillNotice(oldTurns, skillToolOffered),
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

            // WHAT TRUNCATION IS ABOUT TO REMOVE, computed BEFORE it removes it. This path inserts no
            // summary at all, so without its own notice the loss goes unannounced on exactly the path
            // taken when things are already going wrong.
            //
            // Truncate chooses its own boundary, which is not `cut` — so the range is recomputed
            // rather than reusing oldTurns above, which would name skills that survived.
            var keep = (conversation.Count - pin) / 2;
            var notice = SkillNotice(conversation.GetRange(pin, Math.Max(0, conversation.Count - pin - keep)),
                skillToolOffered);

            Truncate(conversation, pin);

            if (notice.Length > 0)
                conversation.Insert(pin, new ChatMessage
                {
                    Role = "assistant",
                    Content = notice.TrimStart(),
                    Timestamp = DateTimeOffset.UtcNow,
                });

            context.EstimateUsageAfterCompaction();
            return new CompressResult(Summarised: false,
                CharsFreed: Math.Max(0, beforeTruncate - context.TotalChars()));
        }
    }


    /// <summary>
    /// Halves the conversation, dropping the OLDEST messages first — the FALLBACK when summarising
    /// fails.
    ///
    /// <para>Lives here rather than with the slash commands: it is list arithmetic with no UI in it,
    /// and its only caller is the catch block below. It was <c>SessionCommands.Compress</c>, which put
    /// the kernel's degradation path in the presentation layer.</para>
    ///
    /// <para>NO MESSAGE-COUNT FLOOR. There was one — eight — on the reasoning that compression is
    /// lossy and a short conversation has nothing worth losing. But nothing here is automatic: this
    /// runs because the USER typed /compress, or because the context crossed a threshold measured in
    /// TOKENS. A count of messages says nothing about either. Eight messages carrying four large file
    /// reads is exactly the case that needs compressing, and the floor silently declined it — a
    /// no-op the user asked for and did not get, with no way to tell it apart from a compression that
    /// found nothing to do.</para>
    ///
    /// <para>Token pressure is the honest trigger and it is applied by the caller. This routine now
    /// does what it is told.</para>
    /// </summary>
    /// <param name="pinnedHead">
    /// Leading messages that are structural rather than history — the system preamble — and must
    /// survive. This dropped from index 0 unconditionally, so the fallback taken when summarisation
    /// FAILS destroyed the working-directory instructions by the other route.
    /// </param>
    /// <param name="conversation">The messages to trim, edited in place.</param>
    public static void Truncate(List<ChatMessage> conversation, int pinnedHead = 0)
    {
        // Two messages is the smallest thing that can be halved at all; below that there is no older
        // half to drop, which is arithmetic rather than a policy. Counted below the pin: a pinned
        // head plus one turn is not a halvable conversation either.
        if (conversation.Count - pinnedHead < 2) return;

        var keep = (conversation.Count - pinnedHead) / 2;
        var removeCount = conversation.Count - pinnedHead - keep;

        // AND NEVER LEAVE A TOOL RESULT WITHOUT ITS CALL. SafeCut does this for the summarise path;
        // this path had bare arithmetic and did not, so a boundary landing on a tool result deleted
        // the assistant message that CALLED it and kept the answer — a result answering nothing.
        // That orphans the conversation permanently: the provider 400s every subsequent request,
        // ContextOverflow.IsOverflow does not match the error so nothing recovers, and only /clear
        // gets the session back. It is the same hazard the cancellation backfill exists to prevent,
        // reached by the path taken when things are ALREADY going wrong.
        //
        // FORWARD, not backward like SafeCut, because the two compute different ends of the same
        // range: SafeCut chooses where deletion STOPS and walks back to keep a pair together, while
        // this chooses where survival STARTS and walks forward, dropping one more message until the
        // first survivor is not an unanswered result. Walking the wrong way here would preserve the
        // orphan and delete a healthy message instead.
        var start = pinnedHead + removeCount;
        while (start < conversation.Count && conversation[start].ToolCallId is not null)
        {
            start++;
            removeCount++;
        }

        conversation.RemoveRange(pinnedHead, removeCount);
    }

    /// <summary>
    /// The structure the summary must have, adapted from opencode's compaction template.
    ///
    /// <para>A FIXED SHAPE BEATS A LIST OF TOPICS. Ours asked for the right facts but left the form
    /// open, and an open form is one the model fills unevenly — the sections it happens to have
    /// material for get written, the rest silently vanish, and there is no way to tell "nothing was
    /// blocked" from "it forgot to say". Named sections with an explicit "(none)" make the absence
    /// itself a recorded fact.</para>
    ///
    /// <para><c>Next Move</c> is the section we most lacked. We asked for "anything not yet done" — a
    /// list — where what a resumed agent actually needs is an ORDERED first action.</para>
    /// </summary>
    public const string SummaryTemplate = """
        Output exactly the Markdown structure shown inside <template> and keep the section order
        unchanged. Do not include the <template> tags in your response.
        <template>
        ## Objective
        - [one or two brief sentences describing what the user is trying to accomplish]

        ## Important Details
        - [constraints, decisions and why, facts established as true, measurements, exact context
          needed to continue, or "(none)"]

        ## Work State
        ### Completed
        - [finished work and verified facts: files changed by exact path, commands run with their
          outcome, builds and tests with exact pass/fail counts; otherwise "(none)"]

        ### Active
        - [current work, partial changes, or investigation state; otherwise "(none)"]

        ### Blocked
        - [blockers, failing commands with their error text, unknowns, and anything tried that did
          NOT work so it is not attempted again; otherwise "(none)"]

        ## Next Move
        1. [immediate concrete action, or "(none)"]
        2. [next action if known, or "(none)"]

        ## Relevant Files
        - [file or directory path: why it matters, or "(none)"]
        </template>

        Rules:
        - Keep every section, even when empty.
        - Use terse bullets, not prose paragraphs.
        - Preserve exact file paths, symbols, commands, error strings, URLs and identifiers verbatim.
          A vague summary is worse than a short one: "fixed the parser" is useless where
          "IndentShift.cs Refuse() now requires a 2-of-3 majority" is not.
        - Where something is uncertain or was never resolved, say so plainly rather than inventing a
          resolution.
        - Do not mention the summary process or that context was compacted.
        """;

    /// <summary>
    /// The whole compaction prompt: the framing, the template, any previous summary to merge, and the
    /// transcript.
    /// </summary>
    /// <param name="previousSummary">
    /// The summary a previous compaction produced, when there is one.
    ///
    /// <para>WITHOUT THIS, EACH COMPACTION SUMMARISES A SUMMARY. The second pass sees only the first
    /// pass's prose, and detail decays geometrically across a long session — the exact failure that
    /// makes late compactions useless. Feeding it back as something to UPDATE means still-true facts
    /// are preserved rather than re-derived from an ever-thinner source.</para>
    /// </param>
    /// <param name="transcript">The conversation text being summarised.</param>
    public static string BuildPrompt(string transcript, string? previousSummary = null)
    {
        var framing = string.IsNullOrWhiteSpace(previousSummary)
            ? "You are compacting your own working memory. Everything below is about to be REPLACED "
              + "by what you write, and you will keep working from it — so write what you would need "
              + "to continue, not what you would tell someone about the past."
            : "You are compacting your own working memory. Below is the summary you wrote earlier "
              + "and the history since. UPDATE the summary: keep what is still true, drop what has "
              + "been superseded, and merge in the new facts. Everything below is about to be "
              + "REPLACED by what you write, and you will keep working from it.";

        var previous = string.IsNullOrWhiteSpace(previousSummary)
            ? ""
            : $"<previous-summary>\n{previousSummary!.Trim()}\n</previous-summary>\n\n";

        // The tool instruction stays even though the call passes no tools. Belt and braces: a
        // provider that ignores an empty tool list would otherwise read a transcript full of tool
        // calls as a request to make them again.
        return framing + "\n\n"
             + SummaryTemplate + "\n\n"
             + "Reply with the note itself and nothing else. Do not call any tool — the history "
             + "below DESCRIBES tools that were already used; it is not a request to use them "
             + "again.\n\n"
             + previous
             + transcript;
    }

    /// <summary>
    /// The summarising call itself: a plain ChatAsync with no tools — there is nothing to decide, only
    /// text to condense. Uses the SAME provider the goal is using; no separate "summarising model"
    /// config knob (a real question, but a separate one — an unused knob is worse than none).
    /// </summary>
    private static Task<LlmResponse> SummariseAsync(
        List<ChatMessage> oldTurns, ILlmProvider provider, CancellationToken ct,
        string? previousSummary = null)
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
                Content = BuildPrompt(transcript, previousSummary),
            },
        };

        return provider.ChatAsync(request, tools: null, ct);
    }

    /// <remarks>Test seam, kept public rather than internal: the two defects this
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
    /// Whether anything came back at all.
    ///
    /// <para>EMPTINESS ONLY. This checked three things: empty, shorter than twelve characters, and
    /// starting with a refusal ("I cannot", "I'm sorry", "As an AI"). The last two are gone — they
    /// were guesses about the QUALITY of a reply, made by matching English, and the length floor had
    /// already been wrong twice: forty rejected "read Foo.cs, changed the parser" (31 chars), then
    /// twenty rejected "earlier: a summary." (19). Both were real summaries of short exchanges, and
    /// both were caught by existing tests rather than by reasoning. A rule that needs correcting
    /// every time it meets a real reply is not measuring what it claims to.</para>
    ///
    /// <para>What is left is not a judgement. The model returned nothing, so there is nothing to put
    /// in place of what would be discarded — splicing <c>""</c> in would delete half the conversation
    /// and leave no record of it. A terse summary, a vacuous one, even a refusal, is at least
    /// SOMETHING the next turn can read; an empty string is not.</para>
    /// </summary>
    private static bool IsUsableSummary(string? text) => !string.IsNullOrWhiteSpace(text);

    private static string FormatSummary(string? text) =>
        $"{SummaryMarker}{text}]";

    /// <summary>
    /// Names the skills whose bodies are being removed, or "" when none are.
    ///
    /// <para>WHY THIS EXISTS. A loaded skill is an ordinary tool result, so compaction removes it like
    /// anything else — and nothing tells the model. Worse, the summary may well MENTION the load
    /// ("loaded the deployment skill"), leaving a model that believes a skill is in force holding
    /// none of its text, still citing a document it can no longer read. This is the one failure this
    /// feature could not otherwise detect, because from the inside it looks like the model simply
    /// ignoring its instructions.</para>
    ///
    /// <para>OUTSIDE THE SUMMARY'S BRACKET, and that placement is the whole point.
    /// <see cref="ExtractPreviousSummary"/> pulls the bracketed text back out on the NEXT compaction
    /// and feeds it to the summariser to merge — so a notice inside the bracket is text a model will
    /// paraphrase or drop, destroying the determinism that makes it worth having. Outside, it is
    /// literal text that nothing ever rewrites.</para>
    ///
    /// <para>It reads as an instruction rather than a note, because the model's next decision is
    /// whether to reload: a line that only reported the loss would leave it to infer the remedy.</para>
    /// </summary>
    private static string SkillNotice(IReadOnlyList<ChatMessage> removed, bool toolOffered)
    {
        var skills = Skills.SkillLoader.LoadedIn(removed);
        if (skills.Count == 0) return "";

        // THE LOSS IS ALWAYS WORTH SAYING; the remedy is only worth saying when it exists. A line
        // that names a tool this session withheld sends the model to spend a turn discovering that.
        return $"\n\nSkill instructions removed by compaction: {string.Join(", ", skills)}. "
             + (toolOffered
                 ? "You are no longer following them — call skill again if the task still needs one."
                 : "You are no longer following them.");
    }

    /// <summary>The marker that makes a summary recognisable as one on the next compaction.</summary>
    private const string SummaryMarker = "[earlier conversation, summarised: ";

    /// <summary>
    /// The summary a previous compaction left in the head, or null on the first pass.
    ///
    /// <para>Matched on the marker this class writes rather than on position: the head's shape
    /// changes as the conversation grows, and a positional guess would silently pick up an ordinary
    /// assistant message and feed it back as though it were a summary.</para>
    /// </summary>
    private static string? ExtractPreviousSummary(IReadOnlyList<ChatMessage> oldTurns)
    {
        // LAST, not first: if several ever accumulate, the newest is the one still true.
        for (var i = oldTurns.Count - 1; i >= 0; i--)
        {
            var content = oldTurns[i].Content;
            if (content is null || !content.StartsWith(SummaryMarker, StringComparison.Ordinal))
                continue;

            var inner = content[SummaryMarker.Length..];
            if (inner.EndsWith(']')) inner = inner[..^1];
            return inner.Trim();
        }
        return null;
    }
}
