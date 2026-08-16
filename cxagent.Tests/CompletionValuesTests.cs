using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The layer that owns the data answers what its valid values are.
///
/// <para>WHAT IT REPLACED. A lambda in the composition root switched on a source name and reached
/// into a resume store, a provider catalog and a session's own instance to build each answer — the
/// internals of three layers in the one place least equipped to own any of them. It also suppressed
/// the feature: adding a popup meant editing a 1,700-line UI method, so two commands that wanted one
/// (<c>/mode edits</c>, <c>/mcp</c>) never got it despite the mechanism already existing.</para>
/// </summary>
public class CompletionValuesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "values-" + Guid.NewGuid().ToString("N"));

    public CompletionValuesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static SessionPorts Ports() =>
        new() { Observer = new BufferedChatSink(), Tools = new BufferedJobPanel() };

    private static ProviderRegistry Registry(params (string Name, string Model, int? Window)[] instances)
    {
        var providers = instances.ToDictionary(
            i => i.Name, i => (ILlmProvider)new MockLlmProvider(i.Model), StringComparer.Ordinal);
        var windows = instances.ToDictionary(i => i.Name, i => i.Window, StringComparer.Ordinal);
        return ProviderRegistry.FromProviders(providers, instances[0].Name, windows);
    }

    private Session Wired(SessionManager manager, ProviderResolution resolution) =>
        manager.Open(_dir, resolution, Ports(), AgentMode.Single);

    // EVERY CONFIGURED INSTANCE, described by its model — and the one in use marked. That marker is
    // why this answer belongs to the session: a catalog alone cannot say which instance is current.
    [Fact]
    public void Session_OffersEveryInstance_AndMarksTheOneInUse()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var catalog = Registry(("local", "qwen3", 213_000), ("remote", "claude-x", 200_000));
        var resolution = ProviderResolution.ForTesting(new MockLlmProvider("qwen3"), "local")
            with { Providers = catalog };

        var session = Wired(manager, resolution);
        var values = session.Values(CompletionSets.Providers);

        Assert.Equal(["local", "remote"], values.Select(v => v.Name));
        Assert.Contains("qwen3", values[0].Summary);
        Assert.Contains("in use", values[0].Summary);
        Assert.DoesNotContain("in use", values[1].Summary);
    }

    // AUTO ONLY WHEN A CLASSIFIER EXISTS. Offering a mode that cannot work is worse than not
    // offering it, and this is the same rule the error message uses — reaching the palette so the
    // two cannot disagree.
    [Fact]
    public void Session_OffersAutoOnlyWhenAClassifierIsConfigured()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));

        var without = Wired(manager, ProviderResolution.ForTesting(new MockLlmProvider()));
        Assert.DoesNotContain("auto", without.Values(CompletionSets.EditModes).Select(v => v.Name));

        var withClassifier = ProviderResolution.ForTesting(new MockLlmProvider())
            with { ClassifierInstance = "local" };
        var with = manager.Open(Path.Combine(_dir, "b"), withClassifier, Ports(), AgentMode.Single);
        Assert.Contains("auto", with.Values(CompletionSets.EditModes).Select(v => v.Name));
    }

    // NUMBERED FOR TYPING, DESCRIBED BY TITLE — the row a user reads is "3", and what tells them
    // which session it is has to be the summary beside it. Moved here from SessionsCommand when the
    // manager took over answering, because the store is the manager's.
    [Fact]
    public async Task Manager_NumbersSessionsAndDescribesThemByTitle()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });

        var session = Wired(manager, ProviderResolution.ForTesting(provider));

        // A ROW EXISTS ONLY ONCE SOMETHING WAS SAID. Without this the list is empty and Assert.All
        // passes over nothing — a test that cannot fail, which is worse than no test.
        await session.Host!.SendAsync("what is 2+2", CancellationToken.None);

        var values = manager.Values(CompletionSets.Sessions, _dir);

        Assert.NotEmpty(values);
        Assert.Equal("1", values[0].Name);
        Assert.NotEmpty(values[0].Summary);
    }

    // A SET IT DOES NOT OWN IS EMPTY, not an error — that is what lets a caller ask the session and
    // then the manager without knowing which answers what.
    [Fact]
    public void Session_IsEmptyForASetItDoesNotOwn()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = Wired(manager, ProviderResolution.ForTesting(new MockLlmProvider()));

        Assert.Empty(session.Values(CompletionSets.Sessions));
        Assert.Empty(session.Values("nonsense"));
    }

    [Fact]
    public void Manager_IsEmptyForASetItDoesNotOwn()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));

        Assert.Empty(manager.Values(CompletionSets.Providers));
        Assert.Empty(manager.Values("nonsense"));
    }

    // NEVER THROWS. This runs on a keystroke inside layout, where an exception from a locked
    // database would take down the composer rather than produce an empty menu.
    [Fact]
    public void Manager_ReturnsEmptyRatherThanThrowing()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));

        Assert.Empty(manager.Values(CompletionSets.Sessions, workingDirectory: "\0invalid"));
    }

    // NO TOOLSET, NO SERVERS — an ordinary headless arrangement, not a failure.
    [Fact]
    public void Manager_OffersNoMcpServersWhenThereIsNoToolset()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));

        Assert.Empty(manager.Values(CompletionSets.McpServers));
    }
}
