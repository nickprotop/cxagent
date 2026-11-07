using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A finished call, told on the session rather than its host — so a watcher that subscribes once, as a browser's hub
/// does, keeps hearing them after a /model switch or a config apply replaces the host.
/// </summary>
public class SessionCallFinishedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "callfin-" + Guid.NewGuid().ToString("N"));

    public SessionCallFinishedTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    private static async Task CalledATool(Session session, MockLlmProvider model)
    {
        model.EnqueueResponse(new LlmResponse
        {
            StopReason = "tool_use",
            ToolCalls = [new ToolCall { Name = "read_file", Id = Guid.NewGuid().ToString("N"),
                Arguments = JsonSerializer.SerializeToElement(new { path = "missing.txt" }) }],
        });
        model.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("read it"));
        await started.Turn;
    }

    [Fact]
    public async Task A_call_after_the_host_is_replaced_still_reaches_a_watcher_that_subscribed_before()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var model = new MockLlmProvider("m");
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(model),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        var heard = new List<ToolCallReport>();
        session.CallFinished += heard.Add;

        session.ReplaceHost(
            new AgentHost(new AgentHost.AgentRuntime { Provider = model, Tools = new AgentTools(session.Executors!) },
                new RecordingSink(), new NullJobPanel()),
            model, null, session.Executors!);
        await CalledATool(session, model);

        var call = Assert.Single(heard);
        Assert.Equal("read_file", call.ToolName);
    }
}
