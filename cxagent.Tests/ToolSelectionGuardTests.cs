using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The one contradiction the system resolves rather than reports: fan-out asked for, spawn tool
/// withheld.
///
/// <para>There is no planner guard. A `WritesAPlanFile` type gets `write_file` by default like every
/// other agent — no built-in ships a `tools` field — so the contradiction only exists if a user
/// writes `-write_file` on it themselves, which this design treats as their call everywhere else.</para>
/// </summary>
public class ToolSelectionGuardTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "toolguard-" + Guid.NewGuid().ToString("N")[..8]);

    public ToolSelectionGuardTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private (Session Session, BufferedChatSink Said) Wire(ToolSelection? selection, AgentMode mode)
    {
        var session = new Session(_dir);
        var paths = new AppPaths(Path.Combine(_dir, "config"));
        var said = new BufferedChatSink();

        SessionFactory.Wire(session,
            ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SharedServices
            {
                Resume = new SqliteSessionStore(paths),
                History = new UsageHistoryStore(paths),
                Logs = new LogFileManager(paths),
            },
            new SessionPorts
            {
                Observer = said,
                ToolObserver = new BufferedJobPanel(),
                ToolSelection = selection,
            },
            mode);

        return (session, said);
    }

    [Fact]
    public void FanOutWithoutTheSpawnToolFallsBackToSingle()
    {
        // RESOLVED, NOT REPORTED. Leaving the mode at fan-out while nothing can delegate keeps the
        // divergence alive for the session's whole life — /mode saying one thing, the tool list
        // another.
        var (session, _) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.Agent]), AgentMode.FanOut);

        Assert.False(session.Mode.CanDelegate);
    }

    [Fact]
    public void AndItSaysSo()
    {
        var (_, said) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.Agent]), AgentMode.FanOut);

        Assert.Contains(said.Notices, m => m.Contains("single mode"));
    }

    [Fact]
    public void FanOutWithTheSpawnToolIsUntouched()
    {
        var (session, said) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.RunShell]), AgentMode.FanOut);

        Assert.True(session.Mode.CanDelegate);
        Assert.DoesNotContain(said.Notices, m => m.Contains("single mode"));
    }

    [Fact]
    public void NoSelectionLeavesFanOutAlone()
    {
        var (session, said) = Wire(null, AgentMode.FanOut);

        Assert.True(session.Mode.CanDelegate);
        Assert.DoesNotContain(said.Notices, m => m.Contains("single mode"));
    }

    [Fact]
    public void SingleModeSaysNothingEitherWay()
    {
        // The fallback answers a contradiction. Single mode without the spawn tool is agreement,
        // not contradiction, and a line about it would be noise.
        var (session, said) = Wire(new ToolSelection([Tool.Inherited, Tool.Not.Agent]), AgentMode.Single);

        Assert.False(session.Mode.CanDelegate);
        Assert.DoesNotContain(said.Notices, m => m.Contains("single mode"));
    }
}
