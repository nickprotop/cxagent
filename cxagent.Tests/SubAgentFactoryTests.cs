using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a child is wired with, and — more importantly — what it is NOT.
///
/// <para>Every assertion here corresponds to a line in the spec's wiring table that has a named
/// failure if omitted. They are tested individually because an omission is SILENT: a child with no
/// context window still runs, still answers, and simply never compacts until it dies on an overflow
/// several minutes in.</para>
/// </summary>
public class SubAgentFactoryTests
{
    private static SubAgentFactory NewFactory(
        TokenLedger? ledger = null, int maxTurns = 50, int? contextWindow = 200_000,
        PluginRegistry? plugins = null, MockLlmProvider? provider = null) =>
        new(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider ?? Answering(),
            Plugins = plugins ?? PluginRegistry.CreateWithBuiltins(),
            Ledger = ledger ?? new TokenLedger(),
            MaxTurns = maxTurns,
            CompressAbove = 40_000,
            ContextWindow = contextWindow,
        });

    /// <summary>A provider with enough queued answers that a test never runs it dry — an empty
    /// queue throws from inside the loop and would hide whichever failure is being tested.</summary>
    private static MockLlmProvider Answering()
    {
        var provider = new MockLlmProvider();
        for (var i = 0; i < 8; i++)
            provider.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        return provider;
    }

    /// <summary>
    /// A CHILD'S CONTEXT IS ITS OWN. The self-containment guarantee: two agents sharing one
    /// conversation is the failure the whole design exists to prevent, and <see cref="Agent"/> gives a
    /// fresh context to anyone who does not pass one.
    /// </summary>
    [Fact]
    public async Task Create_GivesTheChildItsOwnContext()
    {
        var factory = NewFactory();
        var first = factory.Create();
        var second = factory.Create();

        await first.Agent.SendAsync("only the first child hears this", CancellationToken.None);

        Assert.Contains(first.Agent.Context.Messages,
            m => m.Content.Contains("only the first child", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Agent.Context.Messages,
            m => m.Content.Contains("only the first child", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE WINDOW GOES IN AT CONSTRUCTION or never. <c>AgentContext.Window</c> is get-only, so a
    /// factory that forgets it produces a child whose occupancy reads zero, whose
    /// <c>IsUnderPressure</c> is permanently false, and which therefore never compacts however long
    /// it runs — a silent failure that only shows up as a context overflow minutes in.
    /// </summary>
    [Fact]
    public void Create_GivesTheChildTheContextWindow_SoItCanCompact()
    {
        var child = NewFactory(contextWindow: 128_000).Create();

        Assert.Equal(128_000, child.Agent.Context.Window);
    }

    /// <summary>
    /// THE PARENT'S LEDGER (D7). A child's spend is the session's spend: the budget covers the work,
    /// not the agent that happened to do it. With its own ledger a child spends against nothing and
    /// the breach warning never fires.
    /// </summary>
    [Fact]
    public async Task Create_RecordsTheChildsSpend_IntoTheGivenLedger()
    {
        var ledger = new TokenLedger();
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" }
            with { Usage = new LlmUsage { InputTokens = 300, OutputTokens = 40 } });

        var child = NewFactory(ledger: ledger, provider: provider).Create();
        await child.Agent.SendAsync("work", CancellationToken.None);

        Assert.Equal(340, ledger.TotalTokens);
    }

    /// <summary>
    /// SINK ISOLATION — the parent's transcript sees NOTHING of the child's stream.
    ///
    /// <para>Interleaving a child's tokens into the parent's transcript is what makes fan-out
    /// illegible: with several children nothing marks which agent said what. The child gets a row;
    /// its words go to the buffer.</para>
    /// </summary>
    [Fact]
    public async Task Create_CapturesTheChildsOutput_RatherThanRenderingIt()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "what the child found", StopReason = "end_turn" });

        var child = NewFactory(provider: provider).Create();
        var result = await child.Agent.SendAsync("go and look", CancellationToken.None);

        Assert.Equal("what the child found", result.Text);
        Assert.Contains("what the child found", child.Sink.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OTHER HALF OF ISOLATION, and the half that is easy to forget: a buffered chat sink with
    /// the PARENT'S job panel would draw a row in the parent's transcript for every tool the child
    /// calls, interleaved with the parent's own rows and unattributed.
    /// </summary>
    [Fact]
    public async Task Create_CapturesTheChildsToolRows_RatherThanRenderingThem()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "c1",
                    Name = "read_file",
                    Arguments = System.Text.Json.JsonDocument.Parse("""{"path":"nope.txt"}""").RootElement,
                },
            ],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "could not read it", StopReason = "end_turn" });

        var child = NewFactory(provider: provider).Create();
        await child.Agent.SendAsync("read a file", CancellationToken.None);

        Assert.Single(child.Jobs.Jobs);
    }

    /// <summary>
    /// A CHILD CANNOT SPAWN, STRUCTURALLY. Not a rule it is asked to follow — a tool it was never
    /// given. This asserts the mechanism rather than the intent: the factory passes no spawner, so no
    /// amount of prompting makes nesting reachable.
    /// </summary>
    [Fact]
    public async Task Create_GivesTheChildNoWayToSpawn()
    {
        var provider = Answering();
        var child = NewFactory(provider: provider).Create();

        await child.Agent.SendAsync("go", CancellationToken.None);

        // WHAT THE CHILD WAS ACTUALLY OFFERED, read off the provider — not an assertion that the
        // object exists. A model cannot call a tool it was never sent, so this is the property that
        // makes no-nesting true: it holds however the child is prompted.
        Assert.NotNull(provider.LastTools);
        Assert.DoesNotContain(provider.LastTools!,
            t => t.Name.Contains("spawn", StringComparison.OrdinalIgnoreCase)
              || t.Name.Contains("agent", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// THE BRIEFING REACHES THE CHILD'S SYSTEM PROMPT, last — what an agent was created to do is the
    /// most specific instruction there is, so it outranks the general prompt above it.
    /// </summary>
    [Fact]
    public async Task Create_PutsTheBriefingInTheChildsSystemPrompt()
    {
        var child = NewFactory().Create(briefing: "Find every caller of ParseHeader.");
        await child.Agent.SendAsync("go", CancellationToken.None);

        var system = Assert.Single(child.Agent.Context.Messages.Where(m => m.Role == "system"));
        Assert.Contains("ParseHeader", system.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// ZERO MEANS UNBOUNDED, inherited from <see cref="Agent"/> rather than reimplemented here.
    ///
    /// <para>Taken literally a cap of zero stops the agent before its first call — and NOT harmlessly:
    /// the cap path makes a real provider call to salvage a summary, so it costs a request and returns
    /// a plausible summary of a run that never happened.</para>
    /// </summary>
    [Fact]
    public async Task Create_WithZeroMaxTurns_DoesNotCapImmediately()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "a real answer", StopReason = "end_turn" });

        var child = NewFactory(maxTurns: 0, provider: provider).Create();
        var result = await child.Agent.SendAsync("go", CancellationToken.None);

        Assert.Equal(SendOutcome.Completed, result.Outcome);
        Assert.Equal("a real answer", result.Text);
    }

    /// <summary>
    /// TWO CHILDREN ARE TWO AGENTS. Ids key log directories and job rows, so a collision would merge
    /// two children's diagnostics into one unreadable directory.
    /// </summary>
    [Fact]
    public void Create_MintsADistinctIdPerChild()
    {
        var factory = NewFactory();
        Assert.NotEqual(factory.Create().Agent.Id, factory.Create().Agent.Id);
    }

    // --- what the child reports it ran on ---

    /// <summary>
    /// instance:model, the label the ledger keys by. This row is written to the same history database
    /// the session rows are, so the two must agree — a bare model name here reads as a second model
    /// in any stat that groups them.
    /// </summary>
    [Fact]
    public void Create_ReportsTheInstanceAlongsideTheModel()
    {
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = Answering(),
            Plugins = PluginRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            InstanceName = "local",
        });

        Assert.Equal("local:mock-model", factory.Create().ModelId);
    }

    /// <summary>
    /// A TYPE THAT NAMES ITS OWN PROVIDER REPORTS THAT ONE — the whole point of carrying the label.
    /// Two instances can serve one model, so a child routed from `local` to `small` differs only in
    /// the instance; reporting the bare model would describe the interesting case as no change.
    /// </summary>
    [Fact]
    public void Create_WhenATypeRoutesElsewhere_ReportsThatInstance()
    {
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = Answering(),
            Plugins = PluginRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            InstanceName = "local",
        });

        var elsewhere = new AgentType("planner", "brief",
            new TypeRouting(Answering(), 32_000, "small"));

        Assert.Equal("small:mock-model", factory.Create(type: elsewhere).ModelId);
    }

    /// <summary>With no instance name there is nothing to qualify with, and the bare model is still
    /// better than nothing — this is the path a test stub or an unnamed provider takes.</summary>
    [Fact]
    public void Create_WithNoInstanceName_ReportsTheBareModel()
    {
        Assert.Equal("mock-model", NewFactory().Create().ModelId);
    }
}
