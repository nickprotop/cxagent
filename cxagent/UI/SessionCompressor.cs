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
    public readonly record struct CompressResult(bool Summarised);

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

        var keep = conversation.Count / 2;
        var cut = conversation.Count - keep;
        var oldTurns = conversation.GetRange(0, cut);

        try
        {
            var summary = await SummariseAsync(oldTurns, provider, ct);

            meter?.Invoke(summary.Usage);

            conversation.RemoveRange(0, cut);
            conversation.Insert(0, new ChatMessage
            {
                Role = "assistant",
                Content = FormatSummary(summary.Text),
                Timestamp = DateTimeOffset.UtcNow,
            });
            return new CompressResult(Summarised: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Summarisation failed — fall back to the SAME truncation /compress uses, never a
            // second, divergent degradation path.
            SessionCommands.Compress(conversation);
            return new CompressResult(Summarised: false);
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
