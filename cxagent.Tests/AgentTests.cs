using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The three properties extracting <see cref="Agent"/> exists to create: one identity for the
/// agent's whole life, distinct identities between agents, and one context that grows across
/// prompts instead of being rebuilt per message.
/// </summary>
public class AgentTests
{
    /// <summary>The id is the agent's, for its whole life — not one per prompt. It keys the log
    /// directory and the job rows, and a fresh one per message is what fragmented the logs.</summary>
    [Fact]
    public async Task Id_IsStableAcrossPrompts()
    {
        var agent = NewAgent();
        var first = agent.Id;
        await agent.SendAsync("hello", CancellationToken.None);
        await agent.SendAsync("again", CancellationToken.None);
        Assert.Equal(first, agent.Id);
    }

    /// <summary>Two agents are two agents. A sub-agent must not collide with its parent's log
    /// directory or job rows.</summary>
    [Fact]
    public void Id_DiffersBetweenAgents()
    {
        Assert.NotEqual(NewAgent().Id, NewAgent().Id);
    }

    /// <summary>The context is one growing conversation, not rebuilt per prompt.</summary>
    [Fact]
    public async Task Context_GrowsAcrossPrompts()
    {
        var agent = NewAgent();
        await agent.SendAsync("first", CancellationToken.None);
        var afterFirst = agent.Context.Count;
        await agent.SendAsync("second", CancellationToken.None);
        Assert.True(agent.Context.Count > afterFirst,
            $"context did not grow across prompts ({afterFirst} then {agent.Context.Count})");
    }

    /// <summary>The answer comes back from the call rather than being appended to a list the caller
    /// passed in — the transcript is the UI's, and the agent hands it a value.</summary>
    [Fact]
    public async Task SendAsync_ReturnsTheAnswerText()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "the answer", ToolCalls = [], Usage = new LlmUsage() });

        var answer = await NewAgent(provider).SendAsync("a question", CancellationToken.None);

        Assert.Equal("the answer", answer);
    }

    /// <summary>
    /// EDITING CXAGENT.md MID-SESSION TAKES EFFECT ON THE NEXT PROMPT.
    ///
    /// <para>The instruction files are re-read every prompt. That is the user's call to make: they
    /// edited the file, and an agent that silently ignores it until a restart is behaving as though
    /// it knows better. The cache is still protected because the system message is REPLACED only when
    /// the text actually differs — unchanged files produce a byte-identical prefix.</para>
    /// </summary>
    [Fact]
    public async Task SendAsync_RereadsProjectInstructions_WhenTheyChangeMidSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxa-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir);
            var agent = NewAgent();

            await agent.SendAsync("first", CancellationToken.None);
            Assert.DoesNotContain(agent.Context.Messages,
                m => m.Role == "system" && m.Content.Contains("BRAND NEW RULE", StringComparison.Ordinal));

            File.WriteAllText(Path.Combine(dir, "CXAGENT.md"), "BRAND NEW RULE: prefer tabs.");
            await agent.SendAsync("second", CancellationToken.None);

            var system = Assert.Single(agent.Context.Messages.Where(m => m.Role == "system"));
            Assert.Contains("BRAND NEW RULE", system.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// THE TURN CAP IS PER REQUEST, NOT PER SESSION — matching opencode, whose <c>let step = 0</c>
    /// lives inside the per-prompt <c>runLoop</c> (<c>session/prompt.ts:1085</c>).
    ///
    /// <para>A session-wide counter would tighten the ceiling with every message: the second prompt
    /// would start with the first prompt's turns already spent against it, and a long conversation
    /// would eventually be unable to do any work at all. <c>_turn</c> — the field — is monotonic for
    /// a different job, numbering log files across the agent's life.</para>
    /// </summary>
    [Fact]
    public async Task TheTurnCap_AppliesPerRequest_NotAcrossTheSession()
    {
        var provider = new MockLlmProvider();
        // Two tool-calling turns per prompt would hit a cap of 2 if the count carried over.
        for (var i = 0; i < 8; i++)
            provider.EnqueueResponse(new LlmResponse { Text = "ok", ToolCalls = [], Usage = new LlmUsage() });

        var sink = new RecordingSink();
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            sink, new NullJobPanel(), logs: null, maxTurns: 2);

        await agent.SendAsync("first", CancellationToken.None);
        await agent.SendAsync("second", CancellationToken.None);
        await agent.SendAsync("third", CancellationToken.None);

        // Each prompt used one turn. A session-wide counter would have hit the cap by the third.
        Assert.DoesNotContain(sink.Errors, e => e.Contains("stopped after", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A stub provider answering with plain text and no tool calls, in the style of
    /// <c>TestProviders.cs</c>. Enough responses queued that a prompt-per-test never runs the mock
    /// dry — an empty queue is a different failure and would hide the one being tested.
    /// </summary>
    /// <summary>A stub provider with enough queued answers that a test never runs it dry — an empty
    /// queue is a different failure and would hide the one being tested.</summary>
    private static MockLlmProvider NewProvider()
    {
        var provider = new MockLlmProvider();
        for (var i = 0; i < 8; i++)
            provider.EnqueueResponse(new LlmResponse
                { Text = "ok", ToolCalls = [], Usage = new LlmUsage() });
        return provider;
    }

    private static Agent NewAgent(MockLlmProvider? provider = null)
    {
        provider ??= NewProvider();

        return new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50);
    }
    // ---- self-containment ----------------------------------------------------------------------

    /// <summary>
    /// TWO AGENTS DO NOT SHARE A CONTEXT. The whole point of the refactor: an agent constructed
    /// without one gets its OWN, so a sub-agent can never append to its caller's conversation.
    ///
    /// <para>The constructor takes an optional AgentContext, and an optional shared-mutable
    /// parameter is exactly the shape that becomes accidental sharing — a call site that forgets it
    /// would otherwise get the caller's list by default. It gets a fresh one instead.</para>
    /// </summary>
    [Fact]
    public async Task TwoAgents_DoNotShareAContext()
    {
        var first = NewAgent();
        var second = NewAgent();

        await first.SendAsync("only the first agent hears this", CancellationToken.None);

        Assert.Contains(first.Context.Messages,
            m => m.Content.Contains("only the first agent", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Context.Messages,
            m => m.Content.Contains("only the first agent", StringComparison.Ordinal));
    }

    /// <summary>
    /// EVERY AGENT GETS ITS OWN SYSTEM PROMPT, at position 0 of its own context.
    ///
    /// <para>A sub-agent that inherited none would be a model told nothing about its working
    /// directory, its platform or how to verify — and the failure would be silent, showing up as an
    /// agent that behaves subtly worse than its caller for no visible reason.</para>
    /// </summary>
    [Fact]
    public async Task EveryAgent_GetsItsOwnSystemPromptAtPositionZero()
    {
        var first = NewAgent();
        var second = NewAgent();

        await first.SendAsync("hello", CancellationToken.None);
        await second.SendAsync("hello", CancellationToken.None);

        foreach (var agent in new[] { first, second })
        {
            Assert.Equal("system", agent.Context.Messages[0].Role);
            Assert.Contains("Working directory", agent.Context.Messages[0].Content,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The system message is PINNED, so compaction can never summarise it away.
    ///
    /// <para>Losing it mid-session would leave the agent working without the instructions that keep
    /// it on real paths — and it would happen precisely on the longest sessions, where the damage is
    /// worst.</para>
    /// </summary>
    [Fact]
    public async Task TheSystemPrompt_IsPinnedAgainstCompaction()
    {
        var agent = NewAgent();
        await agent.SendAsync("hello", CancellationToken.None);

        Assert.Equal(1, agent.Context.PinnedHeadCount);
    }

    /// <summary>
    /// An agent HANDED a context adopts it rather than starting fresh — which is what makes resume
    /// work, and is the one case where sharing is deliberate rather than accidental.
    /// </summary>
    [Fact]
    public async Task AnAgentGivenAContext_UsesThatOne()
    {
        var context = new CxAgent.Core.Llm.AgentContext();
        context.Add(new CxAgent.Core.Models.ChatMessage
        {
            Role = "user", Content = "restored from an earlier session",
        });

        var agent = new Agent(NewProvider(), PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50, context: context);

        await agent.SendAsync("carry on", CancellationToken.None);

        Assert.Contains(agent.Context.Messages,
            m => m.Content.Contains("restored from an earlier session", StringComparison.Ordinal));
    }
}
