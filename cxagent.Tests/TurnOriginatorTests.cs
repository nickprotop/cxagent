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

    /// <summary>
    /// A PLUGIN'S MAIL THAT WAKES THE SESSION IN A TURN'S TAIL starts a lap that is the plugin's. The
    /// tail is the window after the loop's last look and before the turn is released: what arrives there
    /// starts a lap of its own, and that lap is what an unwire has to be able to sever.
    /// </summary>
    [Fact]
    public async Task A_plugins_mail_in_the_tail_starts_a_lap_that_is_the_plugins()
    {
        var provider = new RecordingProvider();
        var session = Wired(provider, out var manager);
        using var _ = manager;
        var seen = new List<TurnOriginator?>();
        provider.OnCall = () => { lock (seen) seen.Add(session.CurrentOriginator); };
        var once = 0;
        session.BeforeTurnReleasedForTest = () =>
        {
            if (Interlocked.Exchange(ref once, 1) == 0)
                session.Submit("the plugin's report", origin: TurnOriginator.Plugin("events"),
                    source: Session.SubmitSource.System);
        };

        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("go")).Turn;
        Assert.True(await Poll.Until(() => { lock (seen) return seen.Count == 2; }));
        Assert.True(await Poll.Until(() => !session.TurnOwned));

        Assert.Equal(TurnOriginator.User, seen[0]);
        Assert.True(seen[1]?.IsFrom("events"), $"the mail lap ran as {seen[1]}");
    }

    /// <summary>
    /// A PLUGIN'S GOAL STEERED IN THE TAIL starts a lap that is the plugin's too: the session looked idle,
    /// so the plugin submitted rather than queueing, and found the turn still owned.
    /// </summary>
    [Fact]
    public async Task A_plugins_goal_steered_in_the_tail_starts_a_lap_that_is_the_plugins()
    {
        var provider = new RecordingProvider();
        var session = Wired(provider, out var manager);
        using var _ = manager;
        var seen = new List<TurnOriginator?>();
        provider.OnCall = () => { lock (seen) seen.Add(session.CurrentOriginator); };
        var once = 0;
        session.BeforeTurnReleasedForTest = () =>
        {
            if (Interlocked.Exchange(ref once, 1) == 0)
                session.Submit("the plugin's goal", origin: TurnOriginator.Plugin("events"));
        };

        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("go")).Turn;
        Assert.True(await Poll.Until(() => { lock (seen) return seen.Count == 2; }));
        Assert.True(await Poll.Until(() => !session.TurnOwned));

        Assert.Equal(TurnOriginator.User, seen[0]);
        Assert.True(seen[1]?.IsFrom("events"), $"the steered lap ran as {seen[1]}");
    }

    /// <summary>
    /// WHAT THE USER TYPED IN THE TAIL STAYS THE USER'S, even beside a plugin's goal: a lap carrying both
    /// is one an unwire must not destroy, so mixed text runs as the user's.
    /// </summary>
    [Fact]
    public async Task A_users_steer_beside_a_plugins_goal_in_the_tail_runs_as_the_users()
    {
        var provider = new RecordingProvider();
        var session = Wired(provider, out var manager);
        using var _ = manager;
        var seen = new List<TurnOriginator?>();
        provider.OnCall = () => { lock (seen) seen.Add(session.CurrentOriginator); };
        var once = 0;
        session.BeforeTurnReleasedForTest = () =>
        {
            if (Interlocked.Exchange(ref once, 1) != 0) return;
            session.Submit("the plugin's goal", origin: TurnOriginator.Plugin("events"));
            session.Submit("and what I typed");
        };

        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("go")).Turn;
        Assert.True(await Poll.Until(() => { lock (seen) return seen.Count == 2; }));
        Assert.True(await Poll.Until(() => !session.TurnOwned));

        Assert.Equal(TurnOriginator.User, seen[1]);
    }

    /// <summary>MAIL FROM A PLUGIN AND FROM ANYONE ELSE, read by one lap, makes that lap nobody's in particular.</summary>
    [Fact]
    public async Task A_plugins_mail_beside_anyone_elses_in_the_tail_runs_as_the_users()
    {
        var provider = new RecordingProvider();
        var session = Wired(provider, out var manager);
        using var _ = manager;
        var seen = new List<TurnOriginator?>();
        provider.OnCall = () => { lock (seen) seen.Add(session.CurrentOriginator); };
        var once = 0;
        session.BeforeTurnReleasedForTest = () =>
        {
            if (Interlocked.Exchange(ref once, 1) != 0) return;
            session.Submit("the plugin's report", origin: TurnOriginator.Plugin("events"),
                source: Session.SubmitSource.System);
            session.Submit("a job finished", source: Session.SubmitSource.System);
        };

        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("go")).Turn;
        Assert.True(await Poll.Until(() => { lock (seen) return seen.Count == 2; }));
        Assert.True(await Poll.Until(() => !session.TurnOwned));

        Assert.Equal(TurnOriginator.User, seen[1]);
    }
}
