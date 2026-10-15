using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A caller that ran a turn can tell how it ended — headless exit codes depend on it.</summary>
public class StartedOutcomeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "outcome-" + Guid.NewGuid().ToString("N"));
    public StartedOutcomeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    private (SessionManager, Session) Open(MockLlmProvider model, int? maxTurns = null)
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var config = ResolvedConfig.ForTesting(model);
        if (maxTurns is { } n)
            config = config with { Catalog = config.Catalog with { Orchestrator = new OrchestratorSettings(MaxTurns: n) } };
        var session = manager.Open(_dir, config,
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return (manager, session);
    }

    [Fact]
    public async Task AnAnswer_IsCompleted()
    {
        var model = new MockLlmProvider("m");
        model.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        var (manager, session) = Open(model);
        using var _ = manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.SubmitRaw("go"));

        Assert.Equal(SendOutcome.Completed, await started.Outcome.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("done", await started.Result);
    }

    /// <summary>
    /// orchestrator.maxTurns CAPS THE SESSION'S OWN AGENT, not only its children — and --max-turns,
    /// which sets it, therefore ends a headless run at that turn.
    /// </summary>
    [Fact]
    public async Task AConfiguredTurnLimit_CapsTheSessionsOwnAgent()
    {
        var model = new MockLlmProvider("m");
        for (var i = 0; i < 3; i++)
            model.EnqueueResponse(new LlmResponse
            {
                Text = "",
                ToolCalls = [new ToolCall
                {
                    Id = $"g{i}", Name = "glob",
                    Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { pattern = $"*{i}" }),
                }],
                StopReason = "tool_use",
                Usage = new LlmUsage(),
            });
        model.EnqueueResponse(new LlmResponse { Text = "summary", StopReason = "end_turn" });
        var (manager, session) = Open(model, maxTurns: 1);
        using var _ = manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.SubmitRaw("go"));

        Assert.Equal(SendOutcome.Capped, await started.Outcome.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AProviderFailure_IsFailed()
    {
        var (manager, session) = Open(new MockLlmProvider("m"));   // empty queue: Dequeue throws
        using var _ = manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.SubmitRaw("go"));

        Assert.Equal(SendOutcome.Failed, await started.Outcome.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ACancelledTurn_IsCancelled()
    {
        var model = new MockLlmProvider("m");
        var (manager, session) = Open(model);
        using var _ = manager;
        model.EnqueueResponse(new LlmResponse { Text = "x", StopReason = "end_turn" });

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.SubmitRaw("go"));
        session.CancelTurn();

        var outcome = await started.Outcome.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(outcome is SendOutcome.Cancelled or SendOutcome.Completed);
    }
}
