using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The model's own count reaches the context even while the conversation is short.</summary>
public class ContextReadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ctx-reading-" + Guid.NewGuid().ToString("N"));
    public ContextReadingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    // THE PROMPT AND TOOLS ARE MOST OF A SHORT REQUEST: 6,917 tokens for a one-line message is the model
    // counting what it was sent, and the context must keep it rather than call it impossible.
    [Fact]
    public async Task AShortConversation_KeepsTheModelsCount()
    {
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn", Usage = new LlmUsage { InputTokens = 6_917 } });
        var manager = SessionManager.Create(new AppPaths(_dir));
        using var _m = manager;
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(model),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);

        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("hi")).Turn;

        Assert.Equal(6_917, session.Host!.Context.Used);
    }
}
