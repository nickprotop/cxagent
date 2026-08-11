using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The spawn tool end to end: dispatch, the envelope, and — the part that matters most — what happens
/// when a child fails.
/// </summary>
public class SubAgentSpawnerTests
{
    private static MockLlmProvider Answering(params string[] answers)
    {
        var provider = new MockLlmProvider();
        foreach (var a in answers)
            provider.EnqueueResponse(new LlmResponse { Text = a, StopReason = "end_turn" });
        return provider;
    }

    private static SubAgentFactory FactoryOver(ILlmProvider provider) =>
        new(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            logs: null, maxTurns: 50, compressAbove: 40_000, contextWindow: 200_000,
            globalInstructionsDir: null, mcp: null);

    private static ToolCall SpawnCall(string prompt = "find the thing", string description = "find thing") =>
        new()
        {
            Id = "call-1",
            Name = "spawn_agent",
            Arguments = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(new { description, prompt })).RootElement,
        };

    /// <summary>A name it does not own is declined with null, so the dispatch chain falls through to
    /// MCP and then the built-ins — the same contract McpToolset.TryInvokeAsync holds.</summary>
    [Fact]
    public async Task TryInvokeAsync_ForAnotherTool_DeclinesWithNull()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("x")));

        var result = await spawner.TryInvokeAsync(
            new ToolCall { Id = "c", Name = "read_file", Arguments = default }, null, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>The child's answer comes back inside the envelope, with its id and its state.</summary>
    [Fact]
    public async Task TryInvokeAsync_ReturnsTheChildsAnswer_InTheEnvelope()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("it is in Parser.cs:88")));

        var result = await spawner.TryInvokeAsync(SpawnCall(), null, CancellationToken.None);

        Assert.Contains("it is in Parser.cs:88", result!, StringComparison.Ordinal);
        Assert.Contains("state=\"completed\"", result!, StringComparison.Ordinal);
        Assert.Contains("<sub_agent id=", result!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CAPPED CHILD SAYS SO, and says what it means.
    ///
    /// <para>The text on that path is a salvage summary of unfinished work. Reporting it as
    /// <c>completed</c> is precisely the failure the envelope exists to prevent, and the note is there
    /// because "capped" alone is a word the parent's model would have to interpret.</para>
    /// </summary>
    [Fact]
    public async Task TryInvokeAsync_WhenTheChildIsCapped_SaysSoAndWarns()
    {
        var provider = new MockLlmProvider();
        for (var i = 0; i < 4; i++)
            provider.EnqueueResponse(new LlmResponse
            {
                Text = "",
                StopReason = "tool_use",
                ToolCalls = [new ToolCall { Id = $"t{i}", Name = "read_file",
                    Arguments = System.Text.Json.JsonDocument.Parse($$"""{"path":"f{{i}}.txt"}""").RootElement }],
            });
        provider.EnqueueResponse(new LlmResponse { Text = "got partway", StopReason = "end_turn" });

        var factory = new SubAgentFactory(provider, PluginRegistry.CreateWithBuiltins(),
            new TokenLedger(null), logs: null, maxTurns: 2, compressAbove: 40_000,
            contextWindow: 200_000, globalInstructionsDir: null, mcp: null);

        var result = await new SubAgentSpawner(factory).TryInvokeAsync(SpawnCall(), null, CancellationToken.None);

        Assert.Contains("state=\"capped\"", result!, StringComparison.Ordinal);
        Assert.Contains("NOT a completed answer", result!, StringComparison.Ordinal);
    }

    /// <summary>A prompt is the one thing a child cannot run without, and the error says what to
    /// do about it rather than throwing.</summary>
    [Fact]
    public async Task TryInvokeAsync_WithNoPrompt_ReturnsAnError_RatherThanThrowing()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("x")));

        var call = new ToolCall
        {
            Id = "c",
            Name = "spawn_agent",
            Arguments = System.Text.Json.JsonDocument.Parse("""{"description":"do a thing"}""").RootElement,
        };

        var result = await spawner.TryInvokeAsync(call, null, CancellationToken.None);

        Assert.Contains("error:", result!, StringComparison.Ordinal);
        Assert.Contains("prompt", result!, StringComparison.Ordinal);
    }

    /// <summary>The child is handed to the caller BEFORE it runs — the seam telemetry attaches to,
    /// and how the row learns which child it is showing.</summary>
    [Fact]
    public async Task TryInvokeAsync_HandsTheChildToTheCaller_BeforeItRuns()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")));

        SubAgent? seen = null;
        await spawner.TryInvokeAsync(SpawnCall(), c => seen = c, CancellationToken.None);

        Assert.NotNull(seen);
        Assert.False(string.IsNullOrEmpty(seen!.Agent.Id));
    }

    /// <summary>The description becomes the child's briefing — what it was created to do.</summary>
    [Fact]
    public async Task TryInvokeAsync_PutsTheDescriptionInTheChildsBriefing()
    {
        var spawner = new SubAgentSpawner(FactoryOver(Answering("done")));

        SubAgent? child = null;
        await spawner.TryInvokeAsync(SpawnCall(description: "audit the parser"), c => child = c,
            CancellationToken.None);

        var system = Assert.Single(child!.Agent.Context.Messages.Where(m => m.Role == "system"));
        Assert.Contains("audit the parser", system.Content, StringComparison.Ordinal);
    }

    /// <summary>Throws from inside the spawn branch, standing in for anything that can go wrong in a
    /// child — a provider fault, a bad config, a bug.</summary>
    private sealed class ThrowingSpawner : ISubAgentSpawner
    {
        public string ToolName => "spawn_agent";
        public ToolDefinition Definition => new(ToolName, "spawns", default);
        public Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild, CancellationToken ct)
            => throw new InvalidOperationException("the child exploded");
    }

    /// <summary>
    /// A CHILD FAILURE MUST NOT BRICK THE PARENT'S SESSION. The single most important test in step 1.
    ///
    /// <para>The assistant message carrying the tool calls is appended BEFORE they run, so an
    /// exception unwinding the loop leaves a tool call with no matching result. That orphan is
    /// PERMANENT: an orphan 400 is not a length error, so the overflow recovery never matches it, and
    /// compaction only fires on measured pressure a small orphaned context never reaches. Every later
    /// prompt then fails with the provider's 400 and nothing recovers it but /clear — and it presents
    /// on the turn AFTER the failure, which is what makes it hard to diagnose.</para>
    ///
    /// <para>So the assertion is not "the error was reported" but "the context is still well-formed":
    /// every tool call has its result, and the next prompt works.</para>
    /// </summary>
    [Fact]
    public async Task AChildThatThrows_LeavesTheParentsContextWellFormed_AndTheNextTurnWorks()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "",
            StopReason = "tool_use",
            ToolCalls = [SpawnCall()],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "the child failed, here is what I know", StopReason = "end_turn" });
        provider.EnqueueResponse(new LlmResponse { Text = "a later answer", StopReason = "end_turn" });

        var parent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50,
            spawner: new ThrowingSpawner());

        // The turn completes rather than throwing.
        var first = await parent.SendAsync("delegate something", CancellationToken.None);
        Assert.Equal(SendOutcome.Completed, first.Outcome);

        // EVERY TOOL CALL HAS ITS RESULT. This is the orphan check, and it is what the provider's
        // 400 would otherwise be complaining about.
        var toolCallIds = parent.Context.Messages
            .Where(m => m.ToolCalls is { Count: > 0 })
            .SelectMany(m => m.ToolCalls!)
            .Select(c => c.Id ?? c.Name)
            .ToList();
        var resultIds = parent.Context.Messages
            .Where(m => m.Role == "tool")
            .Select(m => m.ToolCallId)
            .ToList();

        Assert.NotEmpty(toolCallIds);
        Assert.All(toolCallIds, id => Assert.Contains(id, resultIds));

        // And the session keeps working, which is the property a user would notice losing.
        var second = await parent.SendAsync("carry on", CancellationToken.None);
        Assert.Equal("a later answer", second.Text);
    }

    /// <summary>
    /// THE TOOL DESCRIPTION CARRIES THE WHEN-NOT-TO (D25), because that is the part that stops a model
    /// delegating work it should simply do. Asserted rather than assumed: the description is the only
    /// place this guidance exists, so losing it in an edit would be silent.
    /// </summary>
    [Fact]
    public void Definition_TellsTheModelWhenNotToSpawn()
    {
        var definition = new SubAgentSpawner(FactoryOver(Answering("x"))).Definition;

        Assert.Contains("Do NOT use it", definition.Description, StringComparison.Ordinal);
        Assert.Contains("cannot spawn sub-agents of its own", definition.Description, StringComparison.Ordinal);
        Assert.Contains("NOT shown to the user", definition.Description, StringComparison.Ordinal);
    }
}
