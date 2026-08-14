using CxAgent.Core.Models;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

public class ChatMarshallingTests
{
    [Fact]
    public async Task Sink_AppendFromBackgroundThread_IsDeferred_NotAppliedSynchronously()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
        var chat = new ChatTranscriptControl();
        var sink = new ChatTranscriptSink(system, chat);

        // BeginAssistantTurn + appends are all enqueued (deferred), never applied on the calling thread.
        var id = sink.BeginAssistantTurn();
        await Task.Run(() =>
        {
            for (int i = 0; i < 50; i++) sink.AppendAssistant(id, "x");
        });

        // The sink marshalled every call via EnqueueOnUIThread: NOTHING was applied to the control off
        // the UI pump. The transcript has no messages yet (AddMessage itself was enqueued, not run).
        // This proves the sink never mutates a control from the calling/background thread — the invariant.
        // (The actual apply-on-UI-thread landing is exercised by the Task 7 E2E + the tmux smoke-drive,
        // which run the real Run() loop that drains the queue.)
        Assert.Empty(chat.MessageIds);   // ChatTranscriptControl.MessageIds (public IReadOnlyList) — verified
    }

    [Fact]
    public void Sink_DoesNotThrow_WhenCalledOffUiThread()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
        var sink = new ChatTranscriptSink(system, new ChatTranscriptControl());
        var ex = Record.Exception(() =>
        {
            var id = sink.AddUserTurn("hi");
            sink.ShowError("boom");
        });
        Assert.Null(ex);   // enqueue-only; no synchronous control mutation, no throw
    }
}
