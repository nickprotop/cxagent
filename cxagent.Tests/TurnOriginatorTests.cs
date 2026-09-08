using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Who asked for a turn, and whether the session reports it while the turn is running.
///
/// <para>THE WHOLE POINT IS UNWIRE'S TIE-BREAK: a busy session normally defers an unwire, but a plugin
/// that keeps itself busy by submitting must not thereby make itself unrevocable. The originator is
/// what lets unwire tell "the plugin is still working" from "the user is still working" apart.</para>
/// </summary>
public class TurnOriginatorTests
{
    [Fact]
    public void A_turn_records_who_originated_it()
    {
        Assert.Equal(TurnOriginator.User, TurnOriginator.User);
        Assert.NotEqual(TurnOriginator.User, TurnOriginator.Plugin("events"));
        Assert.Equal("events", TurnOriginator.Plugin("events").PluginName);
        Assert.Null(TurnOriginator.User.PluginName);
    }

    /// <summary>Records every prompt the model is asked, and lets the test steer mid-flight.</summary>
    private sealed class RecordingProvider : ILlmProvider
    {
        public List<string> Prompts { get; } = [];
        public Action? OnCall { get; set; }

        public string ProviderId => "rec";
        public string ModelId => "rec-model";
        public string DisplayName => "Rec";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            lock (Prompts)
                Prompts.Add(messages.LastOrDefault(m => m.Role == "user")?.Content ?? "");

            OnCall?.Invoke();
            return Task.FromResult(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true);
        }
    }

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "turn-originator-" + Guid.NewGuid().ToString("N"));

    private Session Wired(ILlmProvider provider, out SessionManager manager)
    {
        Directory.CreateDirectory(_dir);
        manager = SessionManager.Create(new AppPaths(_dir));
        return manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
    }

    [Fact]
    public async Task A_plugin_submitted_turn_reports_the_plugin_as_originator()
    {
        var provider = new RecordingProvider();
        var session = Wired(provider, out var manager);
        using var _ = manager;

        // OBSERVED FROM INSIDE THE TURN, because CurrentOriginator is null again once the turn ends —
        // checking it after the await would pass against a session that never recorded anything.
        TurnOriginator? seen = null;
        provider.OnCall = () => seen = session.CurrentOriginator;

        var outcome = session.Submit("do a thing", origin: TurnOriginator.Plugin("experiment"));
        Assert.IsType<Session.SubmitOutcome.Started>(outcome);
        await ((Session.SubmitOutcome.Started)outcome).Turn;

        Assert.True(seen?.IsFrom("experiment"));
        Assert.Null(session.CurrentOriginator);
    }
}
