using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Core's <see cref="IPluginClient"/> against a real <see cref="Session"/>.
///
/// <para>WHAT THESE COVER: the four shapes a plugin sees — an idle submit that starts a turn, a
/// fire-and-forget submit that returns without waiting, a severed client that refuses every call, and
/// a submit made while the session is busy, which the plugin queue takes rather than refusing.</para>
/// </summary>
public class SessionPluginClientTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-client-" + Guid.NewGuid().ToString("N"));

    public SessionPluginClientTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    /// <summary>Answers with a fixed reply, optionally blocking until the test releases it.</summary>
    private sealed class RecordingProvider : ILlmProvider
    {
        public string Reply { get; set; } = "ok";
        public bool BlockUntilReleased { get; set; }

        // A MANUAL GATE, NOT A SLEEP: the test needs the session held busy for an exact window — long
        // enough to exercise the queue path — with no timing guess either side.
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public string ProviderId => "rec";
        public string ModelId => "rec-model";
        public string DisplayName => "Rec";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            if (BlockUntilReleased) await _gate.Task;
            return new LlmResponse { Text = Reply, StopReason = "end_turn" };
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true);
        }
    }

    private Session Wired(ILlmProvider provider, out SessionManager manager)
    {
        manager = SessionManager.Create(new AppPaths(_dir));
        return manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
    }

    [Fact]
    public async Task A_submit_on_an_idle_session_starts_a_turn()
    {
        var session = Wired(new RecordingProvider { Reply = "done" }, out var manager);
        using var _ = manager;
        var client = new SessionPluginClient(session, "experiment", new PluginSubmitQueue());

        var result = await client.Submit("do a thing", wantResult: true);

        Assert.True(result.Accepted);
        Assert.Equal("done", result.Text);
        Assert.Null(result.Refusal);
    }

    [Fact]
    public async Task A_fire_and_forget_submit_does_not_wait_for_the_turn()
    {
        var provider = new RecordingProvider { Reply = "done", BlockUntilReleased = true };
        var session = Wired(provider, out var manager);
        using var _ = manager;
        var client = new SessionPluginClient(session, "experiment", new PluginSubmitQueue());

        var result = await client.Submit("do a thing");   // wantResult defaults to false

        Assert.True(result.Accepted);
        Assert.Null(result.Text);          // it did not wait, so there is nothing to report
        provider.Release();
    }

    [Fact]
    public async Task A_severed_client_refuses_every_call()
    {
        var session = Wired(new RecordingProvider(), out var manager);
        using var _ = manager;
        var client = new SessionPluginClient(session, "experiment", new PluginSubmitQueue());

        client.Sever();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.Submit("too late"));
    }

    [Fact]
    public async Task A_submit_while_busy_is_queued_rather_than_refused()
    {
        var provider = new RecordingProvider { Reply = "first", BlockUntilReleased = true };
        var session = Wired(provider, out var manager);
        using var _ = manager;
        var queue = new PluginSubmitQueue();
        var client = new SessionPluginClient(session, "experiment", queue);

        session.Submit("a user turn");                   // makes the session busy
        var result = await client.Submit("while busy");

        Assert.True(result.Accepted);
        Assert.Equal("while busy", queue.DrainOne()?.Goal);
        provider.Release();
    }
}
