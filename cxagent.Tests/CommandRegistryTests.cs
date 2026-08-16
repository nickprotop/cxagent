using CxAgent.Core.Agent;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// One table, contributed to by both layers.
///
/// <para>WHAT IT REPLACES. Dispatch was a switch on CommandOutcome wrapping chains of
/// <c>if (command.Name == "/x")</c> — about 170 lines in a composition root, so adding a command
/// meant editing the largest method in the codebase. The outcome enum existed to remove that
/// name-matching and could not: nine of thirteen commands declared NeedsWindow, which means "not a
/// prompt" rather than any precondition.</para>
/// </summary>
public class CommandRegistryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "registry-" + Guid.NewGuid().ToString("N"));

    public CommandRegistryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private Session Wired(SessionManager manager, MockLlmProvider? provider = null) =>
        manager.Open(_dir, ProviderResolution.ForTesting(provider ?? new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), Tools = new BufferedJobPanel() },
            AgentMode.Single);

    private static SessionCommand Declared(string name) =>
        SessionCommands.All.Single(c => c.Name == name);

    // THE SESSION IS PASSED, NOT CAPTURED. With one session a handler could close over it and
    // nothing would notice; with tabs, every captured handler would act on whichever session existed
    // when it was registered. This pins that the registry supplies the one the command was typed in.
    [Fact]
    public void TryRun_PassesTheSessionTheCommandWasTypedInto()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var first = Wired(manager);
        var second = manager.Open(Path.Combine(_dir, "b"),
            ProviderResolution.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), Tools = new BufferedJobPanel() },
            AgentMode.Single);

        Session? seen = null;
        manager.Commands.Register(Declared("/help"), (session, _) => { seen = session; return true; });

        Assert.True(manager.Commands.TryRun(second, "/help"));
        Assert.Same(second, seen);

        manager.Commands.TryRun(first, "/help");
        Assert.Same(first, seen);
    }

    // ARGUMENTS ARRIVE WITHOUT THE COMMAND NAME, so a handler parses what was asked rather than
    // re-splitting the whole line.
    [Fact]
    public void TryRun_HandsOverEverythingAfterTheName()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = Wired(manager);

        string? seen = null;
        manager.Commands.Register(Declared("/mcp"), (_, arguments) => { seen = arguments; return true; });

        manager.Commands.TryRun(session, "/mcp show context7");

        Assert.Equal("show context7", seen);
    }

    // NOT A COMMAND IS NOT AN ERROR — the input is a goal, and the caller sends it to the model.
    [Fact]
    public void TryRun_IsFalseForOrdinaryText()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = Wired(manager);

        Assert.False(manager.Commands.TryRun(session, "clear the build output"));
    }

    // A DECLARED COMMAND WITH NO HANDLER YET FALLS THROUGH, which is what lets the migration proceed
    // one command at a time without any of them vanishing from the palette.
    [Fact]
    public void TryRun_IsFalseForACommandNobodyRegistered()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = Wired(manager);

        Assert.False(manager.Commands.TryRun(session, "/init"));
    }

    // SEEDED BY CORE. /clear acts on a session, so the manager can service it without a front end.
    [Fact]
    public async Task Core_SeedsTheCommandsItCanService()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "hi", StopReason = "end_turn" });
        var session = Wired(manager, provider);

        await session.Host!.SendAsync("say hi", CancellationToken.None);
        Assert.NotEmpty(session.Host.Context.Messages);

        Assert.True(manager.Commands.TryRun(session, "/clear"));
        Assert.Empty(session.Host.Context.Messages);
    }

    // LAST REGISTRATION WINS, so a front end can override a seeded command with one that suits it —
    // a host with no transcript might answer /clear differently.
    [Fact]
    public void Register_ReplacesAnEarlierCommandOfTheSameName()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = Wired(manager);

        var overridden = false;
        manager.Commands.Register(Declared("/clear"), (_, _) => { overridden = true; return true; });

        manager.Commands.TryRun(session, "/clear");

        Assert.True(overridden);
    }
}
