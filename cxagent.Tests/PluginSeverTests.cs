using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Unwire cancels a turn the plugin itself started, defers to a turn the user started, and drops
/// what the plugin had queued — the sever half of the plugin contract 3 wiring: a plugin cannot
/// outlive its own revocation by having a turn or a queued goal still running on its behalf.
/// </summary>
public class PluginSeverTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-sever-" + Guid.NewGuid().ToString("N"));

    public PluginSeverTests() => Directory.CreateDirectory(_dir);

    /// <summary>Tolerates a still-landing write, same shape as SessionPluginClientTests.Dispose: a
    /// turn left running past the assertion (the deferred case releases its provider after the
    /// assert) can still be writing when teardown scans the directory.</summary>
    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed class RecordingProvider : ILlmProvider
    {
        public bool BlockUntilReleased { get; set; }

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
            return new LlmResponse { Text = "ok", StopReason = "end_turn" };
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
    public async Task Unwire_cancels_a_turn_the_plugin_itself_started()
    {
        var provider = new RecordingProvider { BlockUntilReleased = true };
        var session = Wired(provider, out var manager);
        using var _ = manager;
        session.Submit("plugin work", origin: TurnOriginator.Plugin("experiment"));
        Assert.True(session.IsBusy);

        var status = await session.UnwirePluginAsync("experiment", CancellationToken.None);

        // A PLUGIN CANNOT OUTLIVE REVOCATION BY SUBMITTING. Its own turn is cancelled and the sever
        // completes; the alternative is a plugin that stays loaded for as long as it keeps working.
        Assert.NotEqual(CommandStatus.Refused, status);
    }

    [Fact]
    public async Task Unwire_defers_to_a_turn_the_user_started()
    {
        var provider = new RecordingProvider { BlockUntilReleased = true };
        var session = Wired(provider, out var manager);
        using var _ = manager;
        session.Submit("the user's own work");           // originator defaults to User
        Assert.True(session.IsBusy);

        var status = await session.UnwirePluginAsync("experiment", CancellationToken.None);

        // SOMEONE ELSE'S WORK IS NOT THE PLUGIN'S TO DISCARD.
        Assert.Equal(CommandStatus.Refused, status);
        provider.Release();
    }

    [Fact]
    public async Task Unwire_defers_when_busy_has_no_originator()
    {
        var provider = new RecordingProvider();
        var session = Wired(provider, out var manager);
        using var _ = manager;

        // PretendBusyForTesting makes the session busy with no turn and therefore no originator —
        // the shape CurrentOriginator?.IsFrom answers false for. A null originator must defer, the
        // same as a user's turn: "nobody claimed this busy state" is not "the plugin claimed it".
        using (session.PretendBusyForTesting())
        {
            var status = await session.UnwirePluginAsync("experiment", CancellationToken.None);
            Assert.Equal(CommandStatus.Refused, status);
        }
    }
}
