using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The commands Core services on its own, dispatched the way the app dispatches them.
///
/// <para>WHY THROUGH TryRun RATHER THAN THE SESSION METHOD. A registration that never fires and a
/// method that never runs look identical from the method's own tests — and both /sessions and /mode
/// moved out of the composition root, where a missed registration is exactly the failure mode. This
/// asserts the whole path: declared, registered, dispatched, and something said.</para>
/// </summary>
public class CoreCommandsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "corecmd-" + Guid.NewGuid().ToString("N"));

    public CoreCommandsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private (SessionManager Manager, Session Session, BufferedChatSink Said) Wired()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var sink = new BufferedChatSink();
        // A RESOLVED CONFIG, because Open without one wires against a null catalog — the sessions
        // and mode commands both read the session's own state, so it has to be a real session.
        var session = manager.Open(_dir,
            ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        return (manager, session, sink);
    }

    // THE LISTING PATH. /sessions with no earlier sessions still answers — silence would read as
    // "the command is broken", which is what an unregistered command looks like.
    [Fact]
    public void Sessions_IsRegisteredInCore_AndSaysSomething()
    {
        var (manager, session, said) = Wired();
        using var _ = manager;

        Assert.True(manager.Commands.TryRun(session, "/sessions"));
        Assert.NotEmpty(said.Transcript);
    }

    // AND SCOPED TO THIS SESSION'S FOLDER, which is why the answer is the session's rather than the
    // manager's even though the manager owns the store.
    [Fact]
    public void Sessions_All_IsAlsoServiced()
    {
        var (manager, session, said) = Wired();
        using var _ = manager;

        Assert.True(manager.Commands.TryRun(session, "/sessions all"));
        Assert.NotEmpty(said.Transcript);
    }

    // /mode LISTS when given nothing, and that listing is the reply the session says itself.
    [Fact]
    public void Mode_IsRegisteredInCore_AndSaysSomething()
    {
        var (manager, session, said) = Wired();
        using var _ = manager;

        Assert.True(manager.Commands.TryRun(session, "/mode"));
        Assert.NotEmpty(said.Transcript);
    }

    /// <summary>
    /// CORE ANSWERS /help, so a front end with no keys to declare needs no help command of its
    /// own — and one that has keys registers over this and prepends them.
    /// </summary>
    [Fact]
    public void Help_IsServicedByCore_AndListsTheCommands()
    {
        var (manager, session, said) = Wired();
        using var _ = manager;

        Assert.True(manager.Commands.TryRun(session, "/help"));
        Assert.Contains("/trust", said.Transcript);
    }

    [Fact]
    public void Init_IsServicedByCore()
    {
        var (manager, session, _) = Wired();
        using var _2 = manager;

        // /init REACHES THE MODEL, so this asserts it was DISPATCHED rather than that a turn ran —
        // a mock provider makes no promise about how quickly a turn completes.
        Assert.Equal(CommandRegistry.Dispatch.Ran, manager.Commands.Run(session, "/init"));
    }

    /// <summary>
    /// THE FRONT END'S OWN COMMANDS, and only those. /mcp needs the manager, token store and HTTP
    /// client a composition root owns, which is not reachable from a session. Every other command
    /// in Core's table must have a Core handler, or a headless consumer silently loses it: the
    /// registry answers NoHandler and the text never reaches a model.
    ///
    /// <para>/exit IS NOT HERE, deliberately: it left Core's table entirely, so a process that
    /// never registers it has no third state to report for it — SessionCommands.All simply does
    /// not name it.</para>
    /// </summary>
    [Fact]
    public void OnlyTheFrontEndsOwnCommandsAreUnhandled()
    {
        var (manager, session, _) = Wired();
        using var _2 = manager;

        var unhandled = SessionCommands.All
            .Where(c => manager.Commands.Run(session, c.Name)
                        == CommandRegistry.Dispatch.NoHandler)
            .Select(c => c.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "/mcp" }, unhandled);
    }
}

/// <summary>
/// Setting a mode REMEMBERS it for the folder — the half whose absence is silent, and the half a
/// call site is free to forget when persisting takes a second call.
///
/// <para>Both callers reach this: <c>/mode edits X</c> and Shift+Tab. A pair of lines at each site
/// is the pair that gets copied with the policy half missing, so the pair is one call, and this is
/// what pins it.</para>
/// </summary>
public class ModePersistenceTests : IDisposable
{
    private readonly string _cfg =
        Path.Combine(Path.GetTempPath(), "modep-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "modew-" + Guid.NewGuid().ToString("N"));

    public ModePersistenceTests()
    {
        Directory.CreateDirectory(_cfg);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cfg)) Directory.Delete(_cfg, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    [Fact]
    public void SetMode_RemembersTheEditModeForTheFolder()
    {
        var rules = new PermissionRulesStore(new AppPaths(_cfg));
        var policy = new PermissionPolicy(_work, rules, EditMode.AlwaysAsk);

        policy.RememberEdits(EditMode.Auto);

        Assert.Equal(EditMode.Auto, rules.GetEditMode(_work));
    }

    // THROUGH THE SESSION, which is the path both callers actually take.
    [Fact]
    public void SetMode_ThroughTheSession_Persists()
    {
        using var manager = SessionManager.Create(new AppPaths(_cfg));
        var session = manager.Open(_work,
            ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        // The policy is what carries the folder; without one there is nothing to remember against,
        // which is itself worth knowing if this ever returns false.
        session.NotePolicy(new PermissionPolicy(_work, manager.Rules!, EditMode.AlwaysAsk));

        Assert.Equal(CommandStatus.Changed, session.SetMode(new WorkingMode(AgentMode.Single, EditMode.AcceptEdits)));
        Assert.Equal(EditMode.AcceptEdits, manager.Rules!.GetEditMode(_work));
    }
}
