using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The four levels, composed in order.
///
/// <para>These drive a real session through SessionFactory, because the composition happens across
/// SharedServices, SessionPorts, Submit and the spawn call — four places that a unit test over
/// ToolSelection alone cannot exercise together.</para>
/// </summary>
public class ToolSelectionLevelsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "toolsel-" + Guid.NewGuid().ToString("N")[..8]);

    public ToolSelectionLevelsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    /// <summary>Records the tools it was offered, and lets a test act mid-turn — the only way to
    /// reach the QUEUED path, which needs a Submit while a turn is genuinely running.</summary>
    private sealed class HookedProvider : ILlmProvider
    {
        public Action? OnCall { get; set; }
        public List<ToolDefinition>? LastTools { get; private set; }

        public string ProviderId => "hook";
        public string ModelId => "hook-model";
        public string DisplayName => "Hook";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            LastTools = tools;
            OnCall?.Invoke();
            return Task.FromResult(new LlmResponse
            {
                Text = "ok",
                StopReason = "end_turn",
                Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
            });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true);
        }
    }

    private static LlmResponse Done() => new()
    {
        Text = "done",
        StopReason = "end_turn",
        Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
    };

    private Session Wire(ILlmProvider provider, ToolSelection? manager = null, ToolSelection? session = null)
    {
        var s = new Session(_dir);
        var paths = new AppPaths(Path.Combine(_dir, "config"));

        SessionFactory.Wire(s,
            ResolvedConfig.ForTesting(provider),
            new SharedServices
            {
                Resume = new SqliteSessionStore(paths),
                History = new UsageHistoryStore(paths),
                Logs = new LogFileManager(paths),
                ToolSelection = manager,
            },
            new SessionPorts
            {
                Observer = new BufferedChatSink(),
                ToolObserver = new BufferedJobPanel(),
                ToolSelection = session,
            },
            AgentMode.Single);

        return s;
    }

    private static async Task<IReadOnlyList<string>> Offered(
        Session session, MockLlmProvider provider, ToolSelection? turn = null)
    {
        provider.EnqueueResponse(Done());
        await session.Host!.RunAsync("go", CancellationToken.None, turn);
        return [.. (provider.LastTools ?? []).Select(t => t.Name)];
    }

    [Fact]
    public async Task NoLevelSpeaksSoEverythingIsOffered()
    {
        var provider = new MockLlmProvider();
        var offered = await Offered(Wire(provider, null, null), provider);

        Assert.Contains(Tool.RunShell, offered);
        Assert.Contains(Tool.TodoWrite, offered);
    }

    [Fact]
    public async Task TheManagerLevelNarrowsEverySession()
    {
        var provider = new MockLlmProvider();
        var session = Wire(provider, new ToolSelection([Tool.Inherited, Tool.Not.RunShell]), null);

        Assert.DoesNotContain(Tool.RunShell, await Offered(session, provider));
    }

    [Fact]
    public async Task TheSessionLevelNarrowsFurther()
    {
        var provider = new MockLlmProvider();
        var session = Wire(provider,
            new ToolSelection([Tool.Inherited, Tool.Not.RunShell]),
            new ToolSelection([Tool.Inherited, Tool.Not.WriteFile]));

        var offered = await Offered(session, provider);

        Assert.DoesNotContain(Tool.RunShell, offered);
        Assert.DoesNotContain(Tool.WriteFile, offered);
        Assert.Contains(Tool.ReadFile, offered);
    }

    [Fact]
    public async Task ALaterLevelMayReopenWhatAnEarlierOneClosed()
    {
        // Levels apply in ORDER, not as an intersection. The old design made the manager a floor;
        // what replaces it is that a selection is only ever written in config or code, never by a
        // model — so a reopening is always a person changing their own deployment.
        var provider = new MockLlmProvider();
        var session = Wire(provider,
            new ToolSelection([Tool.Inherited, Tool.Not.RunShell]),
            new ToolSelection([Tool.Also.RunShell]));

        Assert.Contains(Tool.RunShell, await Offered(session, provider));
    }

    [Fact]
    public async Task TheTurnLevelAppliesToTheSessionsOwnAgent()
    {
        // THE CACHING BUG. If the composed selection were built at construction, S3 would never
        // apply to the top-level agent — the caller most likely to use it — and every other test
        // here would still pass.
        var provider = new MockLlmProvider();
        var session = Wire(provider, null, null);

        var offered = await Offered(session, provider,
            new ToolSelection([Tool.Inherited, Tool.Not.RunShell]));

        Assert.DoesNotContain(Tool.RunShell, offered);
    }

    [Fact]
    public async Task ATurnSelectionDoesNotLeakIntoTheNextRequest()
    {
        var provider = new MockLlmProvider();
        var session = Wire(provider, null, null);

        await Offered(session, provider, new ToolSelection([Tool.ReadFile]));

        Assert.Contains(Tool.RunShell, await Offered(session, provider));
    }

    [Fact]
    public async Task AQueuedSubmitWithTheSAMESelectionReportsNothingIgnored()
    {

        // NOT BLIND. A front end holding one selection for the session forwards it on every submit;
        // flagging each mid-turn correction would be noise that trains people past the flag.
        // TWO SEPARATE INSTANCES with identical terms — passing the same object would pass under
        // reference equality and prove nothing.
        var provider = new HookedProvider();
        var session = Wire(provider);

        Session.SubmitOutcome? queued = null;
        provider.OnCall = () =>
        {
            provider.OnCall = null;
            queued = session.Submit("and also this", tools: new ToolSelection([Tool.ReadFile]));
        };

        var started = Assert.IsType<Session.SubmitOutcome.Started>(
            session.Submit("go", tools: new ToolSelection([Tool.ReadFile])));
        await started.Turn;

        Assert.False(Assert.IsType<Session.SubmitOutcome.Queued>(queued).ToolsIgnored);
    }

    [Fact]
    public async Task AQueuedSubmitWithADIFFERENTSelectionReportsItIgnored()
    {

        // The text is still queued — a correction typed mid-turn is the normal case, not an error —
        // but the selection could not be applied, and silently dropping an argument is what this
        // whole feature exists to prevent.
        var provider = new HookedProvider();
        var session = Wire(provider);

        Session.SubmitOutcome? queued = null;
        provider.OnCall = () =>
        {
            provider.OnCall = null;
            queued = session.Submit("and also this", tools: new ToolSelection([Tool.RunShell]));
        };

        var started = Assert.IsType<Session.SubmitOutcome.Started>(
            session.Submit("go", tools: new ToolSelection([Tool.ReadFile])));
        await started.Turn;

        Assert.True(Assert.IsType<Session.SubmitOutcome.Queued>(queued).ToolsIgnored);
    }

    [Fact]
    public void TheQueuedReceiptDefaultsToNothingIgnored()
    {
        // A caller that passes no selection must never see the flag set, whatever else happens.
        Assert.False(new Session.SubmitOutcome.Queued().ToolsIgnored);
        Assert.True(new Session.SubmitOutcome.Queued(true).ToolsIgnored);
    }
}
