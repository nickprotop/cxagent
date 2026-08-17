using CxAgent.Core.Agent;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Llm.Providers;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A headless session whose whole configuration is built in code.
///
/// <para>WHAT THIS PROVES THAT THE MINIMAL APP DOES NOT. The minimal app shows an agent can be
/// driven; this shows the library can be EMBEDDED — that a host with its own providers, its own
/// agent types, its own turn budgets and its own answer to every permission question can run one
/// without a config.json, a window, a terminal or a person. Nothing below reads a file the library
/// had to find, and nothing below can block waiting for a human.</para>
///
/// <para>THE ONE FILE IT DOES TOUCH is its own state directory — logs, the resume database, the
/// usage archive. That is the manager's, given to it as AppPaths, and a host embedding cxagent gets
/// to choose where it lives.</para>
/// </summary>
public class HeadlessSessionTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "headless-" + Guid.NewGuid().ToString("N"));

    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "headless-work-" + Guid.NewGuid().ToString("N"));

    public HeadlessSessionTests()
    {
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    /// <summary>
    /// Everything a config.json would carry, assembled by the host: two providers with their own
    /// context windows, which of them is default, a custom sub-agent type, and the orchestrator's
    /// turn budget. Plus a gate that answers from a policy instead of a person.
    /// </summary>
    [Fact]
    public async Task AHeadlessHostConfiguresEverythingItself()
    {
        // 1. THE MODELS. A host already knows these from its own settings and has no reason to write
        //    them into JSON for cxagent to read back.
        var fast = new MockLlmProvider("fast-model");
        fast.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        var deep = new MockLlmProvider("deep-model");

        var catalog = ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["fast"] = fast, ["deep"] = deep },
            defaultName: "fast",
            windows: new Dictionary<string, int?> { ["fast"] = 32_000, ["deep"] = 200_000 });

        // 2. THE REST OF THE CONFIGURATION. Agent types this host offers, and the turn budget its
        //    sessions run under — the two things config.json carries that are not about models.
        // THE TWO HALVES, NAMED. What the session starts on, and what the process was configured
        // with — separate types, so a later /model replaces the first and cannot disturb the second.
        var config = new ResolvedConfig(
            new ActiveModel(fast, "fast", "Fast", ContextWindow: 32_000),
            new ProviderCatalog(
                Instances: catalog,
                AgentTypes: new Dictionary<string, AgentTypeConfig>
                {
                    ["surveyor"] = new("Read the tree and report what is there. Never write.",
                        Description: "reads, never writes"),
                },
                Orchestrator: new OrchestratorSettings(MaxTurns: 25)),
            []);

        // 3. THE ANSWER TO EVERY PERMISSION QUESTION, from a policy rather than a person. A headless
        //    host must never block on a prompt, so it decides — here: reads yes, everything else no.
        var refusals = new List<string>();

        using var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_state),
            Config = config,
            BuildGate = _ => new PolicyGate(refusals),
        });

        // 4. WHERE OUTPUT GOES. No window, no terminal — a buffer the host reads afterwards.
        var sink = new BufferedChatSink();

        var session = manager.Open(_work,
            new SessionPorts { Observer = sink, Tools = new BufferedJobPanel() });

        await session.Host!.SendAsync("summarise this folder", CancellationToken.None);

        // THE ROUND TRIP CLOSED: the model's answer reached the host's own buffer.
        Assert.Contains("done", sink.Body);

        // AND THE HOST'S CONFIGURATION REACHED THE SESSION — both providers offered, its own agent
        // type spawnable, neither read from any file.
        Assert.Equal(["fast", "deep"], session.Values(CompletionSets.Providers).Select(v => v.Name));
        Assert.Contains("surveyor", session.Values(CompletionSets.AgentTypes).Select(v => v.Name));
    }

    /// <summary>
    /// NOTHING BLOCKS ON A HUMAN. The gate is a function, so a host answers instantly and the run
    /// never waits — the property that makes a session usable from a service or a cron job.
    /// </summary>
    [Fact]
    public async Task AHeadlessRunNeverWaitsForAPerson()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });

        var refusals = new List<string>();
        using var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_state),
            Config = ResolvedConfig.ForTesting(provider),
            BuildGate = _ => new PolicyGate(refusals),
        });

        // A REAL TURN COMPLETES. If anything waited on a person this would never return — the test
        // would hang rather than fail, which is why the assertion is on the answer arriving.
        var session = manager.Open(_work,
            new SessionPorts
            {
                Observer = new BufferedChatSink(),
                Tools = new BufferedJobPanel(),

                // ALWAYS-ASK, so nothing is silently allowed and every write reaches the gate —
                // where this host's policy answers instead of a person.
                Policy = new PermissionPolicy(_work, manager.Rules!, EditMode.AlwaysAsk),
            });

        // ASKED DIRECTLY, rather than hoping the model attempts a write: what is under test is that
        // the gate ANSWERS without a window, and a run that happens not to write proves nothing.
        var write = new PermissionRequest(
            PermissionKind.FileWrite, Path.Combine(_work, "notes.txt"), AlwaysRule: null);

        var allowed = await manager.Shared.Gate!.RequestAsync(write, CancellationToken.None);

        Assert.False(allowed);
        Assert.Contains(refusals, r => r.Contains("notes.txt", StringComparison.Ordinal));

        // AND A READ IS ALLOWED BY THE SAME POLICY, so the gate is deciding rather than refusing
        // everything.
        var read = new PermissionRequest(
            PermissionKind.FileRead, Path.Combine(_work, "notes.txt"), AlwaysRule: null);

        Assert.True(await manager.Shared.Gate!.RequestAsync(read, CancellationToken.None));

        // AND A REAL TURN STILL COMPLETES. If anything waited on a person this would hang.
        await session.Host!.SendAsync("hello", CancellationToken.None);
    }

    /// <summary>
    /// THE SAME SESSION AGAINST A REAL ENDPOINT, spelled out with the values a config.json would
    /// hold — here the local llama.cpp server from this machine's own config:
    ///
    /// <code>
    /// "local": {
    ///   "kind": "openai-compatible",
    ///   "model": "qwen3.6-35b-a3b-ud-iq4_xs.gguf",
    ///   "baseUrl": "http://localhost:8771/v1",
    ///   "contextWindow": 212992
    /// }
    /// </code>
    ///
    /// <para>NOT RUN AS A TEST — it needs a server listening on 8771, so it is a compiled example
    /// rather than an assertion. Compiled, because an example that drifts out of date is worse than
    /// none: this stops building the moment a signature it uses changes.</para>
    ///
    /// <para>WHAT IT SHOWS is that the mock in the test above is the only difference. Swap
    /// MockLlmProvider for the real one and everything else — the catalog, the agent types, the
    /// gate, the ports — is identical.</para>
    /// </summary>
    [Fact(Skip = "Needs a llama.cpp server on :8771. Verified by hand: passed in 2m12s against " +
                 "qwen3.6-35b-a3b-ud-iq4_xs on 2026-08-17.")]
    public async Task AgainstLocalLlamaCpp() =>
        await RunAgainstLocalLlamaCpp(_state, _work);

    private static async Task RunAgainstLocalLlamaCpp(string stateDir, string workDir)
    {
        // 1 AND 2. THE WHOLE CONFIGURATION, in the shape config.json has — each fact stated once.
        //    AgentConfig is the front door; Resolve() produces what the runtime consumes.
        var config = new AgentConfig
        {
            Models =
            {
                ["local"] = new(ProviderKind.OpenAiCompatible, "qwen3.6-35b-a3b-ud-iq4_xs.gguf")
                {
                    BaseUrl = "http://localhost:8771/v1",

                    // NOT DECORATION: the compression threshold derives from it, so a wrong number
                    // compacts far too early or not until the provider refuses the request.
                    ContextWindow = 212_992,

                    // No key — llama.cpp's OpenAI-compatible server checks none. A hosted endpoint
                    // takes it here, from wherever the host keeps its secrets.
                    ApiKey = null,
                },
            },

            DefaultModel = "local",
            MaxTurns = 300,
        }.Resolve();

        // 3. THE PROCESS, and its answer to every permission question.
        using var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(stateDir),
            Config = config,
            BuildGate = _ => new PolicyGate([]),
        });

        // 4. THE SESSION. Output to a buffer the host reads afterwards.
        var sink = new BufferedChatSink();
        var session = manager.Open(workDir,
            new SessionPorts { Observer = sink, Tools = new BufferedJobPanel() });

        await session.Host!.SendAsync("List the files here and say what this project is.",
            CancellationToken.None);

        Console.WriteLine(sink.Body);
    }

    /// <summary>
    /// A gate that answers from a policy: reads allowed, everything else refused and recorded.
    ///
    /// <para>THIS IS THE WHOLE UI DEPENDENCY, replaced. The interactive gate needs a window to ask
    /// in; a headless one needs a rule and somewhere to log what it turned down.</para>
    /// </summary>
    private sealed class PolicyGate(List<string> refusals) : IPermissionGate
    {
        public Task<bool> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            if (request.Kind == PermissionKind.FileRead) return Task.FromResult(true);

            refusals.Add($"{request.Kind}: {request.Display}");
            return Task.FromResult(false);
        }
    }
}
