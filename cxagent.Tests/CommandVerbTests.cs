using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Commands;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A front end contributing an ARGUMENT to a command it does not own — the shape <c>/stats clear</c>
/// needs because deleting a usage archive must be confirmed first, and Core's synchronous handler
/// cannot ask. See <see cref="CommandRegistry.RegisterVerb"/>.
/// </summary>
public class CommandVerbTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "verbcmd-" + Guid.NewGuid().ToString("N"));

    public CommandVerbTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static SessionManager Manager() => SessionManager.Create(new AppPaths(
        Path.Combine(Path.GetTempPath(), "verbcmd-cfg-" + Guid.NewGuid().ToString("N"))));

    private (SessionManager Manager, Session Session, BufferedChatSink Said) Wired()
    {
        var manager = Manager();
        var sink = new BufferedChatSink();
        var session = manager.Open(_dir,
            ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        return (manager, session, sink);
    }

    /// <summary>
    /// A VERB NOBODY REGISTERED IS NOT OFFERED. /stats clear is the case: deleting the usage
    /// archive needs a confirmation, and a handler that returns bool cannot ask one — so Core
    /// declares no such verb and a headless consumer never sees it in help or the palette.
    /// </summary>
    [Fact]
    public void CoreOffersNoClearVerb_ForStats()
    {
        using var manager = Manager();
        var stats = SessionCommands.All.First(c => c.Name == "/stats");

        Assert.DoesNotContain(manager.Commands.ArgumentsOf(stats), a => a.Name == "clear");
    }

    /// <summary>
    /// AND CORE DOES NOT SILENTLY REPORT INSTEAD. Rendering the dashboard for "/stats clear" tells
    /// a user who asked to delete their history that they still have it — the failure this verb
    /// mechanism exists to remove.
    /// </summary>
    [Fact]
    public void CoreRefusesStatsClear_RatherThanRenderingTheDashboard()
    {
        var (manager, session, said) = Wired();
        using var _ = manager;

        Assert.Equal(CommandRegistry.Dispatch.Ran, manager.Commands.Run(session, "/stats clear"));
        Assert.DoesNotContain("## Usage", said.Transcript);
    }

    /// <summary>
    /// AN UNSERVICED ARGUMENT READS THE SAME WHATEVER IT SAYS. A consumer that registers no clear
    /// verb has no more idea what "clear" means than what "bogus" means, and telling a user that
    /// one of them is a real feature it happens to lack is Core naming something it cannot do.
    /// </summary>
    [Fact]
    public void AnUnservicedArgument_IsRefusedLikeAnyOther()
    {
        var (manager, session, saidClear) = Wired();
        using var _ = manager;
        var (_, sessionBogus, saidBogus) = Wired();

        Assert.Equal(CommandRegistry.Dispatch.Ran, manager.Commands.Run(session, "/stats clear"));
        Assert.Equal(CommandRegistry.Dispatch.Ran, manager.Commands.Run(sessionBogus, "/stats bogus"));

        Assert.DoesNotContain("## Usage", saidClear.Transcript);
        Assert.DoesNotContain("## Usage", saidBogus.Transcript);

        // SAME SHAPE, not the same word: "clear" is echoed back only because it is the argument
        // typed, exactly as "bogus" is — neither reply treats "clear" as a feature Core recognises.
        var clearReply = saidClear.Transcript.Replace("clear", "bogus", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(saidBogus.Transcript, clearReply);
    }

    [Fact]
    public void ARegisteredVerb_IsOffered_AndDispatched()
    {
        using var manager = Manager();
        var session = manager.Open(_dir,
            ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        var ran = false;

        manager.Commands.RegisterVerb("/stats",
            new CommandArgument("clear", "delete all usage history, after confirming"),
            (_, _) => { ran = true; return true; });

        var stats = SessionCommands.All.First(c => c.Name == "/stats");
        Assert.Contains(manager.Commands.ArgumentsOf(stats), a => a.Name == "clear");

        Assert.Equal(CommandRegistry.Dispatch.Ran, manager.Commands.Run(session, "/stats clear"));
        Assert.True(ran);
    }

    /// <summary>
    /// THE COMMAND'S OWN HANDLER STILL TAKES EVERYTHING ELSE. A verb intercepts one argument, not
    /// the command — "/stats 30" must still reach Core's reporting half.
    /// </summary>
    [Fact]
    public void AVerbDoesNotSwallowTheCommandsOtherArguments()
    {
        var (manager, session, said) = Wired();
        using var _ = manager;
        var ran = false;

        manager.Commands.RegisterVerb("/stats",
            new CommandArgument("clear", "delete all usage history, after confirming"),
            (_, _) => { ran = true; return true; });

        Assert.Equal(CommandRegistry.Dispatch.Ran, manager.Commands.Run(session, "/stats 30"));
        Assert.False(ran);
        Assert.Contains("## Usage", said.Transcript);
    }

    /// <summary>Help lists a registered verb and omits an unregistered one, because both read the
    /// same view — otherwise the palette and help drift from what dispatch will accept.</summary>
    [Fact]
    public void HelpReflectsRegisteredVerbs()
    {
        using var manager = Manager();

        Assert.DoesNotContain("/stats clear", SessionCommands.HelpLines(manager.Commands));

        manager.Commands.RegisterVerb("/stats",
            new CommandArgument("clear", "delete all usage history, after confirming"),
            (_, _) => true);

        Assert.Contains("/stats clear", SessionCommands.HelpLines(manager.Commands));
    }

    /// <summary>
    /// A COMMAND EXISTS WHERE IT IS REGISTERED. Core ships the vocabulary; a process advertises
    /// only what it can actually do. Listing a command nobody registered means a user finds it in
    /// help, types it, and is told it does not exist here.
    /// </summary>
    [Fact]
    public void HelpListsOnlyRegisteredCommands()
    {
        using var manager = Manager();

        var help = SessionCommands.HelpLines(manager.Commands);

        Assert.Contains("/clear", help, StringComparison.Ordinal);
        Assert.DoesNotContain("/exit", help, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegisteredCommand_AppearsInHelp()
    {
        using var manager = Manager();
        manager.Commands.Register(new SessionCommand("/quit", "leave"), (_, _) => true);

        Assert.Contains("/quit", SessionCommands.HelpLines(manager.Commands),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE PALETTE AGREES WITH HELP, because both read the registry. A palette offering a row
    /// that completes to "not available in this application" is worse than one that omits it.
    /// </summary>
    [Fact]
    public void MatchingOffersOnlyRegisteredCommands()
    {
        using var manager = Manager();

        var offered = SessionCommands.Matching("/", manager.Commands).Select(c => c.Name);

        Assert.DoesNotContain("/exit", offered);
    }

    /// <summary>
    /// A COMMAND THIS PROCESS DOES NOT HAVE IS UNKNOWN, not "declared but unhandled". With /exit
    /// out of Core's table there is no third state left to report.
    /// </summary>
    [Fact]
    public void AnUnregisteredCommand_ReadsAsUnknown()
    {
        var (manager, session, _) = Wired();
        using var _1 = manager;

        Assert.Equal(CommandRegistry.Dispatch.NotACommand,
            manager.Commands.Run(session, "/exit"));
    }

    /// <summary>
    /// CORE LISTS THE SERVERS IT HOLDS. A consumer with no OAuth browser and no config loader still
    /// gets the half of this command that only reads.
    /// </summary>
    [Fact]
    public void Mcp_IsServicedByCore()
    {
        var (manager, session, _) = Wired();
        using var _ = manager;

        Assert.Equal(CommandRegistry.Dispatch.Ran, manager.Commands.Run(session, "/mcp"));
    }

    /// <summary>
    /// AND ITS HOST-ONLY VERBS ARE NOT OFFERED WHERE NOBODY REGISTERS THEM. Reloading re-reads the
    /// config the host supplied, and logging in opens a browser; a consumer that does neither must
    /// not be shown either row.
    /// </summary>
    [Fact]
    public void McpsHostVerbs_AreAbsentUntilRegistered()
    {
        using var manager = Manager();

        var help = SessionCommands.HelpLines(manager.Commands);

        Assert.Contains("/mcp", help, StringComparison.Ordinal);
        Assert.DoesNotContain("/mcp reload", help, StringComparison.Ordinal);
        Assert.DoesNotContain("/mcp login", help, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegisteredMcpVerb_IsOfferedAndDispatched()
    {
        var (manager, session, _) = Wired();
        using var _ = manager;
        var ran = false;

        manager.Commands.RegisterVerb("/mcp",
            new CommandArgument("reload", "re-read config.json and reconnect"),
            (_, _) => { ran = true; return true; });

        Assert.Contains("/mcp reload", SessionCommands.HelpLines(manager.Commands),
            StringComparison.Ordinal);
        Assert.Equal(CommandRegistry.Dispatch.Ran, manager.Commands.Run(session, "/mcp reload"));
        Assert.True(ran);
    }

    /// <summary>
    /// A VERB WHOSE NAME CARRIES A PLACEHOLDER MATCHES ON ITS VERB. The stored row is
    /// "login &lt;name&gt;" because that is what a palette shows; what a user types is
    /// "login context7". Comparing the whole name would never match.
    /// </summary>
    [Fact]
    public void AVerbWithAPlaceholder_MatchesOnItsFirstWord()
    {
        var (manager, session, _) = Wired();
        using var _ = manager;
        var target = "";

        manager.Commands.RegisterVerb("/mcp",
            new CommandArgument("login <name>", "authorise a server", Completes: false),
            (_, arguments) => { target = arguments; return true; });

        Assert.Equal(CommandRegistry.Dispatch.Ran,
            manager.Commands.Run(session, "/mcp login context7"));
        Assert.Contains("context7", target, StringComparison.Ordinal);
    }
}
