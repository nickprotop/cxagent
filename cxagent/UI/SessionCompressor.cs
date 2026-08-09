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
    /// failed and truncation ran instead — the caller (GoalRunner) uses this to say so, because a
    /// silent degradation to today's behaviour is worse than an honest one.</summary>
    /// <param name="Summarised">
    /// True when the work was done properly — either stale tool output was cleared or the model wrote
    /// a summary. False means the summarising call failed and the oldest messages were dropped
    /// unread, which the caller shows in red: a silent degradation to truncation is worse than an
    /// honest one.
    /// </param>
    /// <param name="CharsFreed">Characters reclaimed, for the row and the status bar.</param>
    /// <param name="ResultsCleared">Tool results emptied; zero when this was a summarisation.</param>
    /// <param name="Summary">The summary written, when one was; null for a prune.</param>
    public readonly record struct CompressResult(
        bool Summarised, int CharsFreed = 0, int ResultsCleared = 0, string? Summary = null);

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
    public static async Task<CompressResult> CompressAsync(
        List<ChatMessage> conversation, ILlmProvider provider, CancellationToken ct,
        Action<LlmUsage>? meter = null)
    {
        // NO MESSAGE-COUNT FLOOR — see SessionCommands.Compress for why. This ran only on an explicit
        // /compress or on measured TOKEN pressure, and neither is answered by counting messages: eight
        // messages carrying four large file reads is precisely the case that needs compressing, and
        // the old floor of eight declined it silently.
        //
        // Two is arithmetic, not policy: below that there is no older half to summarise.
        if (conversation.Count < 2)
            return new CompressResult(Summarised: false);

        // THE CHEAP TIER FIRST. Emptying stale tool results costs nothing — no provider call, no
        // seconds of waiting, and no reasoning discarded — and it reclaims the bulk of what fills a
        // window, because a file read is thousands of characters and the oldest ones describe files
        // that have since been edited. Only when that frees too little is it worth paying for a
        // summary. Claude Code documents the same order ("clears older tool outputs first, then
        // summarizes ... if needed"); opencode implements it the same way.
        var pruned = ToolOutputPruner.Prune(conversation);
        if (pruned.Pruned)
            return new CompressResult(Summarised: true, CharsFreed: pruned.CharsFreed,
                ResultsCleared: pruned.ResultsCleared, Summary: null);

        var cut = SafeCut(conversation);
        if (cut <= 0) return new CompressResult(Summarised: false);
        var oldTurns = conversation.GetRange(0, cut);

        try
        {
            var before = TotalChars(conversation);
            var summary = await SummariseAsync(oldTurns, provider, ct);

            meter?.Invoke(summary.Usage);

            conversation.RemoveRange(0, cut);
            conversation.Insert(0, new ChatMessage
            {
                Role = "assistant",
                Content = FormatSummary(summary.Text),
                Timestamp = DateTimeOffset.UtcNow,
            });
            return new CompressResult(Summarised: true,
                CharsFreed: Math.Max(0, before - TotalChars(conversation)),
                Summary: summary.Text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Summarisation failed — fall back to the SAME truncation /compress uses, never a
            // second, divergent degradation path.
            var beforeTruncate = TotalChars(conversation);
            SessionCommands.Compress(conversation);
            return new CompressResult(Summarised: false,
                CharsFreed: Math.Max(0, beforeTruncate - TotalChars(conversation)));
        }
    }

    /// <summary>Total characters of message content — the size compaction actually changes.</summary>
    internal static int TotalChars(IReadOnlyList<ChatMessage> messages)
    {
        var total = 0;
        foreach (var m in messages) total += m.Content?.Length ?? 0;
        return total;
    }

    /// <summary>
    /// The summarising call itself: a plain ChatAsync with no tools — there is nothing to decide, only
    /// text to condense. Uses the SAME provider the goal is using; no separate "summarising model"
    /// config knob (a real question, but a separate one — an unused knob is worse than none).
    /// </summary>
    private static Task<LlmResponse> SummariseAsync(
        List<ChatMessage> oldTurns, ILlmProvider provider, CancellationToken ct)
    {
        var transcript = string.Join("\n", oldTurns.Select(m => $"{m.Role}: {m.Content}"));

        var request = new List<ChatMessage>
        {
            new()
            {
                Role = "user",
                Content =
                    "Summarise the conversation below into a short, factual note for your own later "
                    + "reference. Record FACTS AND OUTCOMES only — what was asked, what was found, "
                    + "what was produced. No narrative, no commentary, no pleasantries. Prefer "
                    + "concrete details (names, counts, paths, values) over vague description, since "
                    + "a follow-up question may depend on exactly those details.\n\n"
                    + transcript,
            },
        };

        return provider.ChatAsync(request, tools: null, ct);
    }

    private static string FormatSummary(string? text) =>
        $"[earlier conversation, summarised: {text}]";
}
