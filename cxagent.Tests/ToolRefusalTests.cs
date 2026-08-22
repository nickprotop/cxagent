using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Two conditions, two answers, across every selectable source.
///
/// <para>WITHHELD IS NOT UNKNOWN. A name nobody owns is "no such tool" — the right answer for a typo,
/// and it makes the model pick a real one. A name this build ships but this agent was not offered is
/// "not available", which must make the model STOP rather than retry variations.</para>
///
/// <para>Before the guard above the ?? chain, only BUILT-INS consulted the selection on dispatch: a
/// withheld skill, todowrite, ask_user, agent or injected tool was hidden from the offer and still
/// ran when called by name. These are the tests for that hole.</para>
/// </summary>
public class ToolRefusalTests
{
    private sealed class EchoTool : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(
            "echo_tool", "echoes", JsonSerializer.SerializeToElement(new { type = "object" }));

        public PermissionRequest? Gate(JobParameters call) => null;

        public int Calls { get; private set; }

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new JobResult { Success = true, Output = { ["content"] = "ran" } });
        }
    }

    private static LlmResponse Done() => new()
    {
        Text = "done",
        StopReason = "end_turn",
        Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
    };

    /// <summary>Calls one tool by name under a selection, and returns what the model was told.</summary>
    private static async Task<string> CallUnder(ToolSelection? selection, string toolName,
        IReadOnlyList<IAgentTool>? injected = null)
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall(toolName, new { }));
        provider.EnqueueResponse(Done());

        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            agentTools: injected, toolSelection: selection);

        await agent.SendAsync("go", CancellationToken.None);

        return (provider.LastMessages ?? [])
            .Where(m => m.Role == "tool")
            .Select(m => m.Content ?? "")
            .LastOrDefault() ?? "";
    }

    private static ToolSelection Without(string name) => new([Tool.Inherited, "-" + name]);

    // --- Condition 1: the name matches nothing that exists -----------------------------

    [Fact]
    public async Task AnUnknownNameGetsNoSuchTool()
        => Assert.Contains("no such tool", await CallUnder(null, "read_files"));

    [Fact]
    public async Task AnUnknownNameIsUnknownEvenUnderASelection()
        => Assert.Contains("no such tool", await CallUnder(Without(Tool.RunShell), "read_files"));

    // --- Condition 2: exists, but this agent was not offered it ------------------------

    [Fact]
    public async Task AWithheldBuiltinIsNotAvailable()
        => Assert.Contains("not available", await CallUnder(Without(Tool.RunShell), Tool.RunShell));

    [Fact]
    public async Task AWithheldTodoWriteIsNotAvailable()
        => Assert.Contains("not available", await CallUnder(Without(Tool.TodoWrite), Tool.TodoWrite));

    [Fact]
    public async Task AWithheldInjectedToolIsNotAvailable()
        => Assert.Contains("not available",
            await CallUnder(Without("echo_tool"), "echo_tool", [new EchoTool()]));

    [Fact]
    public async Task AWithheldInjectedToolDoesNotRUN()
    {
        // THE SECURITY PROPERTY, and the reason this task could not be deferred. Before the guard,
        // a withheld injected tool was hidden from the offer and still executed when called.
        var tool = new EchoTool();

        await CallUnder(Without("echo_tool"), "echo_tool", [tool]);

        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task TheRefusalListsWhatISAvailable()
    {
        var said = await CallUnder(Without(Tool.RunShell), Tool.RunShell);

        Assert.Contains(Tool.ReadFile, said);
        Assert.DoesNotContain("to this role", said);   // the wording the role removal left behind
    }

    // --- MCP is never withheld ---------------------------------------------------------

    [Fact]
    public async Task AnMcpNameIsNeverRefusedAsWithheld()
    {
        // MCP bypasses selection, so an MCP tool is always offered. A name no server owns is simply
        // unknown — the honest first answer, not "withheld".
        Assert.Contains("no such tool", await CallUnder(new ToolSelection([Tool.ReadFile]), "files_read"));
    }

    // --- The default is untouched ------------------------------------------------------

    [Fact]
    public async Task WithNoSelectionNothingIsWithheld()
    {
        var said = await CallUnder(null, Tool.TodoWrite);

        Assert.DoesNotContain("not available", said);
    }
}
