using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That the model can see what IT said, not only what the user asked.
///
/// <para>FOUND IN A LIVE SESSION, and it looked like the model lying. Ask "say something", get
/// "Hello! How can I help you today?", then ask "what have you replied before?" — and be told "This
/// is the first message in our conversation, so I haven't replied to you before." It was telling the
/// truth about what it could see.</para>
///
/// <para>The cause: a turn that ends WITHOUT tool calls returned before appending its own reply to
/// the conversation. The tool-calling path appended one; the plain-answer path did not, so every
/// ordinary conversational turn vanished from history the moment it finished rendering. The user's
/// messages were all present, which is what made it confusing — history was being sent, with one
/// side of it missing.</para>
/// </summary>
public class ConversationHistoryTests
{
    private static LlmResponse Text(string text) => new()
    {
        Text = text,
        StopReason = "end_turn",
        Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
    };

    private static Agent NewAgent(MockLlmProvider provider) =>
        new(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5);

    [Fact]
    public async Task AnAnswerWithNoToolCalls_IsInTheNextTurnsHistory()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Text("Hello! How can I help you today?"));
        provider.EnqueueResponse(Text("Yes — I said hello."));

        var agent = NewAgent(provider);
        await agent.SendAsync("say something", CancellationToken.None);
        await agent.SendAsync("what have you replied before?", CancellationToken.None);

        // THE SECOND REQUEST IS WHAT MATTERS. Whatever the first one sent, the second must carry the
        // answer the model gave to the first — otherwise it is being asked to recall a turn it has
        // no record of.
        Assert.Contains(provider.LastMessages ?? [],
            m => m.Role == "assistant" && (m.Content ?? "").Contains("Hello!"));
    }

    [Fact]
    public async Task BothSidesOfTheExchangeSurvive_InOrder()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Text("first answer"));
        provider.EnqueueResponse(Text("second answer"));

        var agent = NewAgent(provider);
        await agent.SendAsync("first question", CancellationToken.None);
        await agent.SendAsync("second question", CancellationToken.None);

        // ORDER IS THE POINT, not merely presence: a reply appended after the next question would
        // show the model an exchange where it answered before it was asked.
        var conversation = (provider.LastMessages ?? [])
            .Where(m => m.Role is "user" or "assistant")
            .Select(m => m.Content ?? "")
            .ToList();

        var q1 = conversation.FindIndex(c => c.Contains("first question"));
        var a1 = conversation.FindIndex(c => c.Contains("first answer"));
        var q2 = conversation.FindIndex(c => c.Contains("second question"));

        Assert.True(q1 >= 0, "the first question is missing from history");
        Assert.True(a1 > q1, "the first answer is missing, or precedes the question that prompted it");
        Assert.True(q2 > a1, "the second question does not follow the first answer");
    }

    [Fact]
    public async Task AnEmptyAnswerAddsNothing()
    {
        // NOTHING TO REMEMBER. A blank reply is already reported to the user as a failed turn; adding
        // an empty assistant message would pad the context with a turn that said nothing.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Text(""));
        provider.EnqueueResponse(Text("anything"));

        var agent = NewAgent(provider);
        await agent.SendAsync("say something", CancellationToken.None);
        await agent.SendAsync("again", CancellationToken.None);

        Assert.DoesNotContain(provider.LastMessages ?? [],
            m => m.Role == "assistant" && string.IsNullOrWhiteSpace(m.Content));
    }
}
