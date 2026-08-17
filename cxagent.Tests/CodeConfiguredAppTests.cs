using CxAgent.Core.Agent;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Setups past the three-line minimum: a host that configures everything from code, one that runs
/// two sessions on two models, and one that gates.
///
/// <para>WHY THESE ARE PINNED. The minimal app proves an agent can be driven; it does not prove the
/// library can be USED — that a caller with its own providers, its own agent types and its own
/// permission answers can assemble one without a config.json, a window or a terminal. Every one of
/// these is a shape a second front end would need, and none of them had a test.</para>
/// </summary>
public class CodeConfiguredAppTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "coded-" + Guid.NewGuid().ToString("N"));

    public CodeConfiguredAppTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static SessionPorts Ports(ISessionObserver sink) =>
        new() { Observer = sink, Tools = new BufferedJobPanel() };

    private static MockLlmProvider Replying(string text)
    {
        var provider = new MockLlmProvider(text);
        provider.EnqueueResponse(new LlmResponse { Text = text, StopReason = "end_turn" });
        return provider;
    }

    /// <summary>
    /// EVERYTHING FROM CODE, NO config.json. The whole configuration — which providers exist, which
    /// is default, their context windows, the agent types, the orchestrator's budgets — is built by
    /// the caller and handed over. Nothing here touches a file the library would have had to find.
    ///
    /// <para>This is the shape a host embedding cxagent has: it already knows its models, from its
    /// own settings system, and has no reason to write them into a JSON file for cxagent to read
    /// back.</para>
    /// </summary>
    [Fact]
    public async Task AHostCanConfigureEverythingInCode()
    {
        var fast = Replying("from fast");
        var deep = new MockLlmProvider("deep");

        var catalog = ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["fast"] = fast, ["deep"] = deep },
            defaultName: "fast",
            windows: new Dictionary<string, int?> { ["fast"] = 32_000, ["deep"] = 200_000 });

        var config = new ResolvedConfig(fast, "Fast", [])
        {
            InstanceName = "fast",
            ContextWindow = 32_000,
            Providers = catalog,
            AgentTypes = new Dictionary<string, AgentTypeConfig>
            {
                ["surveyor"] = new("Read the tree and report what is there.",
                    Description: "reads, never writes"),
            },
        };

        using var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_dir),
            Config = config,
        });

        var sink = new BufferedChatSink();
        var session = manager.Open(_dir, Ports(sink));

        await session.Host!.SendAsync("hello", CancellationToken.None);

        Assert.Contains("from fast", sink.Body);

        // THE CATALOG REACHED THE SESSION, so the palette and /model see what the host configured
        // rather than what a file said.
        var offered = session.Values(CompletionSets.Providers).Select(v => v.Name).ToList();
        Assert.Equal(["fast", "deep"], offered);

        // AND SO DID THE AGENT TYPES, merged with the shipped ones.
        Assert.Contains("surveyor", session.Values(CompletionSets.AgentTypes).Select(v => v.Name));
    }

    /// <summary>
    /// TWO SESSIONS, TWO MODELS, ONE PROCESS — what tabs are. Each session names its own
    /// configuration; the manager's is the default for anyone who does not.
    /// </summary>
    [Fact]
    public async Task TwoSessionsCanRunDifferentModels()
    {
        var one = Replying("from one");
        var two = Replying("from two");

        using var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_dir),
            Config = ResolvedConfig.ForTesting(one, "one"),
        });

        var firstSink = new BufferedChatSink();
        var secondSink = new BufferedChatSink();

        var a = Directory.CreateDirectory(Path.Combine(_dir, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_dir, "b")).FullName;

        var first = manager.Open(a, Ports(firstSink));                                    // the default
        var second = manager.Open(b, ResolvedConfig.ForTesting(two, "two"), Ports(secondSink));

        await first.Host!.SendAsync("hello", CancellationToken.None);
        await second.Host!.SendAsync("hello", CancellationToken.None);

        Assert.Contains("from one", firstSink.Body);
        Assert.Contains("from two", secondSink.Body);

        // NEITHER SAW THE OTHER'S MODEL, which is the isolation the manager exists to make ordinary.
        Assert.Equal("one", first.InstanceName);
        Assert.Equal("two", second.InstanceName);
    }

    /// <summary>
    /// A HOST THAT ANSWERS PERMISSION QUESTIONS ITSELF. The gate is a hook, so a service with a
    /// policy — allow reads, refuse writes, log everything — supplies one without a window.
    /// </summary>
    [Fact]
    public void AHostCanAnswerPermissionQuestionsWithoutAWindow()
    {
        var asked = new List<string>();

        using var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_dir),
            Config = ResolvedConfig.ForTesting(new MockLlmProvider()),
            BuildGate = rules => new RecordingGate(asked),
        });

        Assert.NotNull(manager.Shared.Gate);

        var session = manager.Open(_dir, Ports(new BufferedChatSink()),
            mode: new WorkingMode(AgentMode.Single, EditMode.AlwaysAsk));

        Assert.NotNull(session.Host);
    }

    /// <summary>A gate that answers from a policy rather than a person.</summary>
    private sealed class RecordingGate(List<string> asked) : IPermissionGate
    {
        public Task<bool> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            asked.Add(request.Display);
            return Task.FromResult(request.Kind == PermissionKind.FileRead);
        }
    }
}
