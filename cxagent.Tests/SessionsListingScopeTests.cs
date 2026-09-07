using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That `/sessions` lists the folder of the session it was typed in.
///
/// <para>THE LISTING IS THE FOLDER GUARD. `/sessions resume` resolves a typed uid against the rows
/// this produced, so a listing showing another project's sessions is not only confusing — it is the
/// route by which a resume reaches a conversation from somewhere else.</para>
/// </summary>
public class SessionsListingScopeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cxagent-listscope-" + Guid.NewGuid().ToString("N"));

    private readonly string _alpha;
    private readonly string _beta;

    public SessionsListingScopeTests()
    {
        _alpha = Path.Combine(_root, "alpha");
        _beta = Path.Combine(_root, "beta");
        Directory.CreateDirectory(_alpha);
        Directory.CreateDirectory(_beta);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A SECOND SESSION LISTS ITS OWN FOLDER, not the first session's.
    ///
    /// <para>Both sessions live in one manager and share one resume store, so the only thing that
    /// separates their listings is the working directory each passes — and that has to come from the
    /// session the command was dispatched with.</para>
    /// </summary>
    [Fact]
    public void EachSessionListsItsOwnFolder()
    {
        using var manager = SessionManager.Create(new AppPaths(_root));

        var store = manager.Shared.Resume!;
        store.SaveTurn("ALPHAAAAAAAAAAAAAAAAAAAAAA", [], 1, 1, workingDir: _alpha);
        store.SaveTurn("BETAAAAAAAAAAAAAAAAAAAAAAA", [], 1, 1, workingDir: _beta);

        var said = new BufferedChatSink();
        var beta = manager.Open(_beta, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = said, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        beta.ListSessions("");

        var reply = said.Transcript;

        Assert.Contains("BETAAA", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("ALPHAA", reply, StringComparison.Ordinal);
    }

    /// <summary>AND `all` WIDENS IT DELIBERATELY — the one way another folder's sessions appear.</summary>
    [Fact]
    public void TheAllVerbListsEveryFolder()
    {
        using var manager = SessionManager.Create(new AppPaths(_root));

        var store = manager.Shared.Resume!;
        store.SaveTurn("ALPHAAAAAAAAAAAAAAAAAAAAAA", [], 1, 1, workingDir: _alpha);
        store.SaveTurn("BETAAAAAAAAAAAAAAAAAAAAAAA", [], 1, 1, workingDir: _beta);

        var said = new BufferedChatSink();
        var beta = manager.Open(_beta, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = said, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        beta.ListSessions("all");

        var reply = said.Transcript;
        Assert.Contains("ALPHAA", reply, StringComparison.Ordinal);
    }
}
