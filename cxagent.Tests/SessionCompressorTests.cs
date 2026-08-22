using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// P11 Task 3: SUMMARISE the oldest turns instead of deleting them. Truncation deleted four goals'
/// worth of findings outright on a live drive — nothing condensed, everything discarded. A summary
/// preserves the referent a follow-up needs ("earlier: listed ~/bin, 6 files, 4 ending .sh"); deletion
/// loses it entirely.
/// </summary>
public class SessionCompressorTests
{
    private static ChatMessage Msg(string role, string content) =>
        new() { Role = role, Content = content };

    /// <summary>A plausible model reply. These tests are about WHERE the cut lands, not about
    /// summary quality — but a 7-character placeholder is not a reply the compressor accepts, and a
    /// fixture that trips the quality gate would be testing the wrong thing.</summary>
    private const string SummaryText = "Earlier: read three files and changed the parser guard.";

    private static LlmResponse Usage(LlmResponse r) =>
        r with { Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5 } };

    [Fact]
    public async Task Compress_ReplacesOldTurnsWithASummary_NotWithNothing()
    {
        // Truncation deleted four goals' worth of findings on a live drive. A summary preserves the
        // referent a follow-up needs: "earlier: listed ~/bin, 6 files, 4 ending .sh".
        var provider = new RecordingProvider(
            Usage(new LlmResponse { Text = "Earlier: listed ~/bin, found 6 files, 4 ending .sh." }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        var text = string.Concat(conversation.Select(m => m.Content));
        Assert.Contains("4 ending .sh", text);        // the FINDING survived
        Assert.True(conversation.Count < 12);          // ...and the conversation actually shrank
    }

    /// <summary>
    /// The working-directory preamble outlives compression.
    ///
    /// <para>It is the message that stops the model guessing absolute paths — measured before it
    /// existed, ten of twenty shell calls hunted for paths that do not exist on this machine
    /// (/Users/&lt;someone&gt;/…, /home/user, bare /). Compression removed from index 0, which is
    /// exactly where it sits, so the FIRST compaction of any session deleted it.</para>
    /// </summary>
    [Fact]
    public async Task Compress_KeepsTheSystemPreamble_AtTheHead()
    {
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = SummaryText }));
        var context = new AgentContext();
        var conversation = context.Messages;
        conversation.Add(Msg("system", "Your working directory is /home/nick/source/cxagent."));
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.Equal("system", conversation[0].Role);
        Assert.Contains("working directory", conversation[0].Content);

        // ...and the summary landed BELOW it, not on top of it.
        Assert.Contains(conversation.Skip(1), m => m.Content.Contains("parser guard"));
    }

    /// <summary>
    /// The truncation fallback must pin the head too. SessionCommands.Compress also removed from
    /// index 0, so a summarisation failure destroyed the preamble by the other route — the same bug
    /// reached by the path taken when things are already going wrong.
    /// </summary>
    [Fact]
    public async Task Compress_WhenSummarisationFails_StillKeepsTheSystemPreamble()
    {
        var provider = new ThrowingProvider();
        var context = new AgentContext();
        var conversation = context.Messages;
        conversation.Add(Msg("system", "Your working directory is /home/nick/source/cxagent."));
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.Equal("system", conversation[0].Role);
        Assert.Contains("working directory", conversation[0].Content);
    }

    /// <summary>
    /// THE FALLBACK MUST NOT ORPHAN A TOOL RESULT. <c>SafeCut</c> walks the summarise path's boundary
    /// backward off any ToolCallId so a call and its result are removed together; <c>Truncate</c> did
    /// bare arithmetic and had no such walk. When its boundary landed on a tool result, the assistant
    /// message that CALLED it was deleted and the result survived — answering nothing.
    ///
    /// <para>That orphan 400s the session permanently: ContextOverflow.IsOverflow does not match it,
    /// so nothing recovers and only /clear gets the session back. The trigger is ordinary — this path
    /// is taken whenever summarisation throws, which is what a provider blip looks like.</para>
    /// </summary>
    [Fact]
    public async Task Compress_WhenSummarisationFails_NeverLeavesAToolResultWithoutItsCall()
    {
        var provider = new ThrowingProvider();
        var context = new AgentContext();
        var conversation = context.Messages;

        // TWO LEADING TURNS. The boundary is arithmetic — for a conversation of N messages the
        // survivors begin at index N - N/2 — so reproducing this is a question of PARITY, not of
        // size: the pairs must be positioned so that index lands on a RESULT rather than on a call.
        // With 18 messages the survivors start at index 9, and these two leaders put a result there.
        // Every even count with pairs starting at index 0 lands on a call instead, which is how the
        // bug survived a suite that already exercises this fallback.
        conversation.Add(Msg("user", "start"));
        conversation.Add(Msg("assistant", "working on it"));

        for (int i = 0; i < 8; i++)
        {
            conversation.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "",
                ToolCalls = [new ToolCall { Name = "read_file", Id = $"call-{i:D2}" }],
            });
            conversation.Add(new ChatMessage
            {
                Role = "tool",
                Content = $"contents-{i:D2}",
                ToolCallId = $"call-{i:D2}",
            });
        }

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        // EVERY surviving result must still have the call it answers, somewhere above it.
        var callIds = conversation
            .Where(m => m.ToolCalls is not null)
            .SelectMany(m => m.ToolCalls!)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = conversation
            .Where(m => m.ToolCallId is not null && !callIds.Contains(m.ToolCallId))
            .Select(m => m.ToolCallId)
            .ToList();

        Assert.Empty(orphans);
    }

    /// <summary>A loaded skill body, as the load tool writes it into the conversation.</summary>
    private static ChatMessage SkillBody(string name, string id) => new()
    {
        Role = "tool",
        Content = $"[skill: {name}]\ndirectory: /tmp/skills/{name}\n\nShip carefully.",
        ToolCallId = id,
    };

    /// <summary>
    /// COMPACTION MUST SAY WHAT IT TOOK. A loaded skill is an ordinary tool result, so it is removed
    /// like anything else and nothing tells the model — worse, the summary may MENTION the load,
    /// leaving a model that believes a skill is in force holding none of its text. From the inside
    /// that looks like a model ignoring its instructions, which is the one failure this could not
    /// otherwise be diagnosed as.
    /// </summary>
    [Fact]
    public async Task Compress_NamesTheSkillsWhoseBodiesItRemoved()
    {
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = SummaryText }));
        var context = new AgentContext();
        var conversation = context.Messages;
        conversation.Add(SkillBody("deployment", "t1"));
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        var text = string.Concat(conversation.Select(m => m.Content));
        Assert.Contains("deployment", text, StringComparison.Ordinal);
        Assert.Contains("skill", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// OUTSIDE THE BRACKET, and this is the whole reason the placement was specified. The NEXT
    /// compaction pulls the bracketed summary back out via ExtractPreviousSummary and feeds it to the
    /// summariser to merge — so a notice inside the bracket is text a model paraphrases or drops,
    /// which destroys the determinism that makes it worth having.
    /// </summary>
    [Fact]
    public async Task Compress_PutsTheSkillNoticeOutsideTheSummaryBracket()
    {
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = SummaryText }));
        var context = new AgentContext();
        var conversation = context.Messages;
        conversation.Add(SkillBody("deployment", "t1"));
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        var summary = conversation.First(m => m.Content.Contains("summarised", StringComparison.Ordinal));
        var closing = summary.Content.IndexOf(']');
        var notice = summary.Content.IndexOf("deployment", StringComparison.Ordinal);

        Assert.True(notice > closing,
            "the notice must follow the closing bracket, or the next compaction feeds it to the "
            + "summariser and a model rewrites it");
    }

    /// <summary>
    /// THE FALLBACK PATH NEEDS ITS OWN NOTICE. It inserts no summary at all, so without this the loss
    /// goes unannounced on exactly the path taken when things are ALREADY going wrong — a provider
    /// blip during compaction.
    /// </summary>
    [Fact]
    public async Task Compress_WhenSummarisationFails_StillNamesTheSkillsItRemoved()
    {
        var provider = new ThrowingProvider();
        var context = new AgentContext();
        var conversation = context.Messages;
        conversation.Add(SkillBody("deployment", "t1"));
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        var text = string.Concat(conversation.Select(m => m.Content));
        Assert.Contains("deployment", text, StringComparison.Ordinal);
        Assert.Contains("skill", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A SKILL THAT SURVIVED IS NOT ANNOUNCED AS LOST. The notice names what the cut actually removed,
    /// so a body in the half that stays must not appear in it — telling a model to reload something
    /// it still holds would waste the window on a second copy.
    /// </summary>
    [Fact]
    public async Task Compress_DoesNotNameASkillThatSurvivedTheCut()
    {
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = SummaryText }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));
        conversation.Add(SkillBody("survivor", "t9"));      // newest — in the half that is kept

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        var summary = conversation.First(m => m.Content.Contains("summarised", StringComparison.Ordinal));
        Assert.DoesNotContain("removed by compaction", summary.Content, StringComparison.Ordinal);
    }

    /// <summary>No skills, no notice — a compaction that removed none must read exactly as before.</summary>
    [Fact]
    public async Task Compress_WithNoSkillsLoaded_AddsNoNotice()
    {
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = SummaryText }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.DoesNotContain("removed by compaction",
            string.Concat(conversation.Select(m => m.Content)), StringComparison.Ordinal);
    }

    /// <summary>
    /// No preamble, no pin. A sub-agent or a test context that never had a system message must still
    /// compress from the very front rather than mysteriously keeping its oldest user turn.
    /// </summary>
    [Fact]
    public async Task Compress_WithNoSystemMessage_StillCompressesFromTheFront()
    {
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = SummaryText }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.DoesNotContain(conversation, m => m.Content.Contains("goal-00"));
        Assert.Contains("parser guard", conversation[0].Content);
    }

    /// <summary>
    /// An empty reply must not become the only record of the discarded half.
    ///
    /// <para>Losing history is acceptable when something was written down — that is the trade
    /// compression makes. Losing it to <c>""</c> is not: the call succeeded, half the conversation
    /// was deleted, and what replaced it says nothing at all.</para>
    /// </summary>
    [Fact]
    public async Task Compress_EmptySummary_LeavesTheConversationUntouched()
    {
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = "   " }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        var result = await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.False(result.Summarised);
        Assert.Equal(12, conversation.Count);
        Assert.Contains(conversation, m => m.Content.Contains("goal-00"));
    }

    /// <summary>
    /// A REFUSAL IS STILL SPLICED IN. There was a check for it — replies starting "I cannot",
    /// "I'm sorry", "As an AI" were rejected — and it is gone: that is a guess about the QUALITY of a
    /// reply made by matching English, the same mistake as the no-write challenge. A refusal is at
    /// least something the next turn can read; an empty string is not, which is the one case still
    /// rejected.
    /// </summary>
    [Fact]
    public async Task Compress_RefusalSummary_IsAcceptedRatherThanSecondGuessed()
    {
        var provider = new RecordingProvider(
            Usage(new LlmResponse { Text = "I cannot summarise this conversation." }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        var result = await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.True(result.Summarised);
        Assert.Contains("I cannot summarise", string.Concat(conversation.Select(m => m.Content)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A terse but REAL note still compresses. The floor exists to exclude "ok" and "done", not to
    /// second-guess a model that summarised four turns in one accurate sentence.
    /// </summary>
    [Fact]
    public async Task Compress_ShortButRealSummary_IsAccepted()
    {
        var provider = new RecordingProvider(Usage(new LlmResponse
        {
            Text = "Read IndentShift.cs; Refuse() now needs a 2-of-3 majority.",
        }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        var result = await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.True(result.Summarised);
        Assert.Contains("2-of-3 majority", string.Concat(conversation.Select(m => m.Content)));
    }

    [Fact]
    public async Task Compress_KeepsTheMostRecentTurnsVERBATIM()
    {
        // The newest turns are the likeliest referent. Summarising them too would lose the precision a
        // follow-up depends on.
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = SummaryText }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.Contains(conversation, m => m.Content.Contains("goal-11"));
    }

    [Fact]
    public async Task Compress_WhenSummarisationFails_FallsBackToTruncation_AndSaysSo()
    {
        // A housekeeping failure must not kill a working session — but it must not be silent either.
        // Truncation is the fallback, never the primary.
        var provider = new ThrowingProvider();
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        var result = await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.True(conversation.Count < 12);           // it still shrank
        Assert.False(result.Summarised);                 // ...but the caller can tell it degraded
    }

    [Fact]
    public async Task Compress_OnASingleMessage_DoesNothing()
    {
        // REPLACES Compress_OnAShortConversation_StillDoesNothing, which pinned a message-count floor
        // of eight. That floor was wrong for both callers: this runs on an explicit /compress or on
        // measured TOKEN pressure, and a count of messages answers neither — eight messages carrying
        // four large file reads is exactly the case that needs compressing, and the floor declined it
        // silently, indistinguishable from a compression that found nothing to do.
        //
        // What remains is arithmetic: one message has no older half to summarise.
        var provider = new RecordingProvider();   // no scripted reply — a call would throw
        var context = new AgentContext();
        context.Add(Msg("user", "one"));
        var conversation = context.Messages;

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.Single(conversation);
    }

    [Fact]
    public async Task Compress_OnAShortConversation_NowCompressesIt()
    {
        // The other half of removing the floor: a short conversation the user asked to compress IS
        // compressed. Two messages is the smallest thing that can be halved at all.
        var provider = new RecordingProvider(
            Usage(new LlmResponse { Text = "read Foo.cs, changed the parser" }));
        var context = new AgentContext();
        context.AddRange([Msg("user", "one"), Msg("assistant", "two")]);
        var conversation = context.Messages;

        var result = await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.True(result.Summarised);
        Assert.Equal(2, conversation.Count);   // one summary + the newest half
        Assert.Contains("changed the parser", conversation[0].Content, System.StringComparison.Ordinal);
    }
    // ---- the compaction prompt -----------------------------------------------------------------

    /// <summary>
    /// THE TEMPLATE'S SECTIONS ALL REACH THE MODEL. Ours asked for the right facts but left the form
    /// open, and an open form is filled unevenly — sections with material get written and the rest
    /// silently vanish, with no way to tell "nothing was blocked" from "it forgot to say".
    /// </summary>
    [Fact]
    public void BuildPrompt_AsksForEverySection()
    {
        var prompt = SessionCompressor.BuildPrompt("some transcript");

        foreach (var section in new[]
                 { "## Objective", "## Important Details", "## Work State",
                   "### Completed", "### Active", "### Blocked",
                   "## Next Move", "## Relevant Files" })
            Assert.Contains(section, prompt, StringComparison.Ordinal);

        // And the rule that makes an empty section a recorded fact rather than an omission.
        Assert.Contains("(none)", prompt, StringComparison.Ordinal);
        Assert.Contains("Keep every section", prompt, StringComparison.Ordinal);
    }

    /// <summary>The transcript is what is being summarised, so it has to be in there.</summary>
    [Fact]
    public void BuildPrompt_IncludesTheTranscript()
    {
        Assert.Contains("MARKER-TRANSCRIPT-TEXT",
            SessionCompressor.BuildPrompt("MARKER-TRANSCRIPT-TEXT"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A PREVIOUS SUMMARY IS FED BACK TO BE UPDATED, not re-summarised. Without it, the second
    /// compaction reads only the first one's prose and detail decays geometrically across a long
    /// session — which is what makes late compactions useless.
    /// </summary>
    [Fact]
    public void BuildPrompt_WithAPreviousSummary_AsksForAnUpdateRatherThanAFreshSummary()
    {
        var prompt = SessionCompressor.BuildPrompt("new history", "OLD-SUMMARY-BODY");

        Assert.Contains("<previous-summary>", prompt, StringComparison.Ordinal);
        Assert.Contains("OLD-SUMMARY-BODY", prompt, StringComparison.Ordinal);
        Assert.Contains("UPDATE", prompt, StringComparison.Ordinal);
        Assert.Contains("still true", prompt, StringComparison.Ordinal);
    }

    /// <summary>And the first pass says nothing about a previous summary — an empty
    /// &lt;previous-summary&gt; block would ask the model to merge with nothing.</summary>
    [Fact]
    public void BuildPrompt_WithNoPreviousSummary_HasNoPreviousSummaryBlock()
    {
        var prompt = SessionCompressor.BuildPrompt("history");

        Assert.DoesNotContain("<previous-summary>", prompt, StringComparison.Ordinal);
    }

    /// <summary>The no-tools instruction survives the rewrite. The call passes no tools, but a
    /// provider that ignores an empty list would read a transcript full of tool calls as a request to
    /// make them again.</summary>
    [Fact]
    public void BuildPrompt_TellsTheModelNotToCallTools()
    {
        Assert.Contains("Do not call any tool",
            SessionCompressor.BuildPrompt("history"), StringComparison.Ordinal);
    }
}
