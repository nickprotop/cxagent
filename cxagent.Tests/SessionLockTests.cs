using System.Diagnostics;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>One live session, one process: the lock a process holds on a session's folder, how others see
/// it, and how it follows the conversation across a re-wire.</summary>
public sealed class SessionLockTests : IDisposable
{
    private readonly string _cfg = Directory.CreateTempSubdirectory("lock-cfg-").FullName;
    private readonly string _work = Directory.CreateTempSubdirectory("lock-work-").FullName;
    private string Logs => new AppPaths(_cfg).LocksDir;

    public void Dispose()
    {
        try { Directory.Delete(_cfg, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_work, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void Second_acquire_of_same_agent_fails()
    {
        using var first = SessionLock.TryAcquire(Logs, "A1", LockHolder.Web);
        Assert.NotNull(first);
        Assert.Null(SessionLock.TryAcquire(Logs, "A1", LockHolder.Terminal));
    }

    [Fact]
    public void Probe_reports_holder_kind_and_pid()
    {
        using var held = SessionLock.TryAcquire(Logs, "A1", LockHolder.Web);
        Assert.Equal(new HeldBy(LockHolder.Web, Environment.ProcessId), SessionLock.Probe(Logs, "A1"));
    }

    [Fact]
    public void Dispose_frees_it()
    {
        SessionLock.TryAcquire(Logs, "A1", LockHolder.Web)!.Dispose();
        Assert.Null(SessionLock.Probe(Logs, "A1"));
        using var again = SessionLock.TryAcquire(Logs, "A1", LockHolder.Terminal);
        Assert.NotNull(again);
    }

    [Fact]
    public void Stale_lock_file_without_holder_is_free()
    {
        Directory.CreateDirectory(Logs);
        File.WriteAllText(Path.Combine(Logs, "A1.lock"), "");
        File.WriteAllText(Path.Combine(Logs, "A1.json"), """{"kind":"web","pid":1}""");
        Assert.Null(SessionLock.Probe(Logs, "A1"));
    }

    [Fact]
    public void Lock_is_free_after_holder_process_dies()
    {
        var holder = Path.Combine(AppContext.BaseDirectory, "cxagent.Tests.LockHolder.dll");
        Assert.True(File.Exists(holder), holder);
        using var child = Process.Start(new ProcessStartInfo("dotnet", [holder, Logs, "A1"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        try
        {
            Assert.Equal("held", child.StandardOutput.ReadLine());
            Assert.Equal(LockHolder.Web, SessionLock.Probe(Logs, "A1")?.Kind);
        }
        finally
        {
            child.Kill();
            child.WaitForExit(10_000);
        }
        using var mine = SessionLock.TryAcquire(Logs, "A1", LockHolder.Terminal);
        Assert.NotNull(mine);
    }

    private (SessionManager Manager, Session Session, SessionPorts Ports, ResolvedConfig Config) OpenOne()
    {
        var manager = SessionManager.Create(new AppPaths(_cfg));
        var policy = new PermissionPolicy(_work, new PermissionRulesStore(new AppPaths(_cfg)), EditMode.AcceptEdits);
        var config = ResolvedConfig.ForTesting(new MockLlmProvider());
        var ports = new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(), Policy = policy };
        var session = manager.Open(_work, config, ports, AgentMode.Single);
        return (manager, session, ports, config);
    }

    [Fact]
    public void Open_takes_the_lock_and_rewire_moves_it()
    {
        var (manager, session, ports, config) = OpenOne();
        using var _ = manager;
        var first = session.SessionId!;
        Assert.NotNull(SessionLock.Probe(Logs, first));

        manager.Open(session, config, ports, AgentMode.Single);   // a re-wire: a new agent, a new folder

        Assert.NotEqual(first, session.SessionId);
        Assert.Null(SessionLock.Probe(Logs, first));
        Assert.NotNull(SessionLock.Probe(Logs, session.SessionId!));
    }

    [Fact]
    public void Close_releases_the_lock()
    {
        var (manager, session, _, _) = OpenOne();
        using var _m = manager;
        var id = session.SessionId!;
        Assert.True(manager.Close(session));
        Assert.Null(SessionLock.Probe(Logs, id));
    }

    [Fact]
    public void Shutdown_releases_the_lock()
    {
        var (manager, session, _, _) = OpenOne();
        var id = session.SessionId!;
        manager.Shutdown(TimeSpan.FromSeconds(5));
        Assert.Null(SessionLock.Probe(Logs, id));
    }

    [Fact]
    public void Session_folder_stays_readable_while_held()
    {
        var (manager, session, _, _) = OpenOne();
        using var _m = manager;
        var folder = new SessionFolder(new AppPaths(_cfg), session.SessionId!).Dir;
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            using (File.OpenRead(file)) { }
    }

    [Fact]
    public void Manager_holder_kind_is_what_others_see()
    {
        var manager = SessionManager.Create(new AppPaths(_cfg));
        manager.Holder = LockHolder.Web;
        using var _ = manager;
        var policy = new PermissionPolicy(_work, new PermissionRulesStore(new AppPaths(_cfg)), EditMode.AcceptEdits);
        var session = manager.Open(_work, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(), Policy = policy },
            AgentMode.Single);
        Assert.Equal(LockHolder.Web, SessionLock.Probe(Logs, session.SessionId!)?.Kind);
    }
}
