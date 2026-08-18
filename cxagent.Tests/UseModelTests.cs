using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Naming a model resolves it from the catalog the session already holds.
///
/// <para>WHAT THIS REPLACED. <c>/model openrouter</c> went out to config.json through
/// <c>ConfigResolver.ResolveInstance</c> — a file read, a re-validation, a full registry rebuild and
/// a window probe — to produce something the process already held in memory, then kept only its
/// <c>.Model</c> and discarded the rest. It was also a SECOND ANSWER SOURCE: the catalog is fixed for
/// the process (F5 restarts rather than reconfiguring in place), so a config.json edited since
/// startup gave <c>/model</c> a different answer than every other part of the session.</para>
/// </summary>
public class UseModelTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "use-" + Guid.NewGuid().ToString("N"));

    public UseModelTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static SessionPorts Ports() =>
        new() { Observer = new BufferedChatSink(), Tools = new BufferedJobPanel() };

    /// <summary>Two named instances with declared windows — the shape of a real config.</summary>
    private static ProviderRegistry TwoInstances(out ILlmProvider first, out ILlmProvider second)
    {
        first = new MockLlmProvider("model-one");
        second = new MockLlmProvider("model-two");
        return ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["one"] = first, ["two"] = second },
            defaultName: "one",
            windows: new Dictionary<string, int?> { ["one"] = 32_000, ["two"] = 128_000 });
    }

    // ---- the registry derives, and that is the single definition -----------------------------------

    [Fact]
    public void Use_DerivesEveryFieldFromTheCatalogEntry()
    {
        var registry = TwoInstances(out _, out var second);

        var model = registry.Use("two");

        Assert.NotNull(model);
        Assert.Same(second, model!.Provider);
        Assert.Equal("two", model.InstanceName);
        Assert.Equal(128_000, model.ContextWindow);
    }

    // A MISS IS A MISS, NOT A FALLBACK. Falling back to reading config would put two answer sources
    // behind one method — the ambiguity that separating ProviderCatalog from ActiveModel ended.
    [Fact]
    public void Use_AnUnknownName_IsNull_AndDoesNotReachForConfig()
    {
        var registry = TwoInstances(out _, out _);

        Assert.Null(registry.Use("no-such-instance"));
        Assert.Null(registry.Use(""));
        Assert.Null(registry.Use(null));
    }

    // ONE DEFINITION. The catalog delegates rather than deriving again, so the two cannot drift.
    [Fact]
    public void TheCatalogDelegates_RatherThanDerivingAgain()
    {
        var registry = TwoInstances(out _, out var second);
        var catalog = new ProviderCatalog(registry);

        var viaCatalog = catalog.Use("two");
        var viaRegistry = registry.Use("two");

        Assert.Equal(viaRegistry!.InstanceName, viaCatalog!.InstanceName);
        Assert.Same(second, viaCatalog.Provider);
        Assert.Equal(viaRegistry.ContextWindow, viaCatalog.ContextWindow);
    }

    // ---- the window, which is the part that travels badly when null -------------------------------

    /// <summary>
    /// THE WINDOW MOVES WITH THE MODEL, and a switch that lost it would be silent.
    ///
    /// <para><c>AgentHost.SwapProvider</c> reads <c>model.ContextWindow ?? _runtime.ContextWindow</c>:
    /// a null window on the incoming model KEEPS the window of the model being left. So a 32k model
    /// switched to a 128k one would compact at the wrong ceiling with nothing said — which is the
    /// bug ContextWindowProbe exists to prevent, reintroduced at the switch rather than at startup.
    /// </para>
    /// </summary>
    [Fact]
    public void Use_CarriesTheWindow_SoTheSwitchDoesNotInheritTheOldOne()
    {
        var registry = TwoInstances(out _, out _);

        Assert.Equal(32_000, registry.Use("one")!.ContextWindow);
        Assert.Equal(128_000, registry.Use("two")!.ContextWindow);
    }

    /// <summary>
    /// AN INSTANCE THAT DECLARED NO WINDOW HAS NOTHING TO ASK, and says so rather than guessing.
    ///
    /// <para>A registry built without config — the mock path, most tests — has no endpoint to probe,
    /// so the honest answer is null. Compaction falls back to a fixed threshold, which the probe's
    /// own summary argues is better than a made-up number.</para>
    /// </summary>
    [Fact]
    public void Use_WithNoDeclaredWindowAndNoEndpoint_IsNull_NotAGuess()
    {
        var registry = ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["bare"] = new MockLlmProvider("m") },
            defaultName: "bare");

        Assert.Null(registry.Use("bare")!.ContextWindow);
    }

    // ---- the session overloads --------------------------------------------------------------------

    /// <summary>By name, resolved from the catalog the session was wired with — no paths, no
    /// environment, which is why this can live on the session at all.</summary>
    [Fact]
    public void Use_ByName_MovesTheSession()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var registry = TwoInstances(out var first, out var second);

        var session = manager.Open(_dir,
            new ResolvedConfig(registry.Use("one"), new ProviderCatalog(registry), []),
            Ports(), AgentMode.Single);

        Assert.True(session.Use("two"));

        Assert.Same(second, session.Provider);
        Assert.Equal("two", session.InstanceName);
        Assert.NotSame(first, session.Provider);
    }

    /// <summary>A name the catalog does not know is refused, and the session says so — one speaker
    /// per outcome, which is why Use returns null rather than announcing anything itself.</summary>
    [Fact]
    public void Use_ByName_RefusesAnUnknownInstance()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var registry = TwoInstances(out var first, out _);

        var session = manager.Open(_dir,
            new ResolvedConfig(registry.Use("one"), new ProviderCatalog(registry), []),
            Ports(), AgentMode.Single);

        Assert.False(session.Use("no-such-instance"));
        Assert.Same(first, session.Provider);   // and it stays where it was
    }

    /// <summary>
    /// THE EXPLICIT OVERLOAD IS NOT REACHABLE THROUGH THE BY-NAME ONE, which is why both exist. A
    /// caller holding a provider the catalog never knew about — a test double, a headless embedder —
    /// has no name to pass.
    /// </summary>
    [Fact]
    public void Use_ByModel_TakesAProviderTheCatalogNeverKnew()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var registry = TwoInstances(out _, out _);

        var session = manager.Open(_dir,
            new ResolvedConfig(registry.Use("one"), new ProviderCatalog(registry), []),
            Ports(), AgentMode.Single);

        var stranger = new MockLlmProvider("not-in-any-catalog");
        Assert.Null(registry.Use("stranger"));        // the catalog cannot reach it

        Assert.True(session.Use(new ActiveModel(stranger, "stranger", "Stranger", 64_000)));
        Assert.Same(stranger, session.Provider);
        Assert.Equal("stranger", session.InstanceName);
    }
}

/// <summary>
/// The window an instance did not declare is ASKED OF THE ENDPOINT, once.
///
/// <para>WHY LAZILY RATHER THAN AT BUILD. Probing every instance at startup costs three seconds per
/// unreachable endpoint for models the user may never select — so <c>Build</c> keeps the coordinates
/// and the question is asked the first time somebody needs the answer.</para>
///
/// <para>WHY CACHED. A user cycling <c>/model</c> between two instances would otherwise re-probe on
/// every switch, adding a network round trip to a keystroke.</para>
///
/// <para>Runs a real HTTP listener rather than mocking one: the probe's whole job is to speak to a
/// server, and the parsing half is already covered against captured payloads elsewhere.</para>
/// </summary>
public class LazyWindowProbeTests
{
    private sealed class FakeEndpoint : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        public int Hits;

        public FakeEndpoint(int port)
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var ctx = await _listener.GetContextAsync();
                        Interlocked.Increment(ref Hits);
                        var body = System.Text.Encoding.UTF8.GetBytes(
                            """{"data":[{"id":"served","meta":{"n_ctx":212992}}]}""");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                    catch (Exception) { return; }   // listener stopped
                }
            });
        }

        public void Dispose() { try { _listener.Stop(); } catch (Exception) { } }
    }

    [Fact]
    public void AnUndeclaredWindow_IsAskedOfTheEndpoint_AndRememberedAfterwards()
    {
        const int port = 18899;
        using var endpoint = new FakeEndpoint(port);

        var settings = new ProviderSettings(
            new Dictionary<string, ProviderInstanceConfig>
            {
                // Declares nothing — the case that must ask.
                ["served"] = new("openai-compatible", "served", null,
                    $"http://127.0.0.1:{port}/v1", null, null, null, false),
                // Declares 4096 — config is the user telling us something, and must win.
                ["declared"] = new("openai-compatible", "other", null,
                    $"http://127.0.0.1:{port}/v1", null, 4096, null, false),
            },
            "served", [], new Dictionary<string, RoutingTarget>(), null);

        var registry = ProviderRegistry.Build(settings);

        // Nothing asked yet: Build keeps coordinates, it does not probe.
        Assert.Null(registry.InstanceWindows["served"]);
        Assert.Equal(0, endpoint.Hits);

        Assert.Equal(212_992, registry.Use("served")!.ContextWindow);
        Assert.Equal(1, endpoint.Hits);

        // ASKED ONCE. The second switch reads the remembered answer.
        Assert.Equal(212_992, registry.Use("served")!.ContextWindow);
        Assert.Equal(1, endpoint.Hits);

        // CONFIGURED FIRST, PROBED SECOND — a declared window is never overridden by the server.
        Assert.Equal(4096, registry.Use("declared")!.ContextWindow);
        Assert.Equal(1, endpoint.Hits);
    }

    // AN UNREACHABLE ENDPOINT IS "UNKNOWN", NOT AN ERROR, and caching that null would turn one
    // momentary outage into a permanently unknown window for the life of the process.
    [Fact]
    public void AnUnreachableEndpoint_IsNull_AndIsNotCachedAsTheAnswer()
    {
        var settings = new ProviderSettings(
            new Dictionary<string, ProviderInstanceConfig>
            {
                ["down"] = new("openai-compatible", "m", null,
                    "http://127.0.0.1:1/v1", null, null, null, false),
            },
            "down", [], new Dictionary<string, RoutingTarget>(), null);

        var registry = ProviderRegistry.Build(settings);

        Assert.Null(registry.Use("down")!.ContextWindow);
        Assert.Null(registry.InstanceWindows["down"]);   // nothing was remembered
    }
}
