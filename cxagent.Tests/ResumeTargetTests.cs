using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which session a resume actually re-wires.
///
/// <para>THE PROCESS-WIDE HOOK RE-WIRES WHOEVER IT WAS BUILT OVER, which is the right answer exactly
/// once. With several sessions, `/sessions resume` typed in the second applied to the FIRST:
/// discarding a live conversation, possibly under its own running turn — the busy check tests the
/// session that ASKED, not the one that gets rebuilt — while the asking session kept a pending
/// snapshot armed and was told the restore had worked.</para>
/// </summary>
public class ResumeTargetTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-resume-" + Guid.NewGuid().ToString("N"));

    public ResumeTargetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private SessionManager Manager() => SessionManager.Create(new AppPaths(_dir));

    private static SessionSnapshot Snapshot() =>
        new("agent-1", [], 0, 0, DateTimeOffset.UtcNow);

    /// <summary>THE PER-SESSION HOOK IS ASKED, and it is told WHICH session.</summary>
    [Fact]
    public void ResumeAsksThePerSessionHookAboutTheSessionBeingResumed()
    {
        using var manager = Manager();
        var session = new Session(_dir);

        Session? asked = null;
        manager.RewireOne = s => { asked = s; return true; };

        manager.Resume(session, Snapshot());

        Assert.Same(session, asked);
    }

    /// <summary>
    /// A REFUSAL ARMS NOTHING. This is the property that matters: a pending snapshot left behind by
    /// a declined resume fires on some unrelated later re-wire, restoring a conversation nobody
    /// asked for at a moment nobody expects.
    /// </summary>
    [Fact]
    public void ARefusedResumeLeavesNoPendingSnapshot()
    {
        using var manager = Manager();
        var session = new Session(_dir);

        manager.RewireOne = _ => false;
        manager.Rewire = () => throw new InvalidOperationException("must not run");

        manager.Resume(session, Snapshot());

        Assert.Null(session.TakePendingResume());
    }

    /// <summary>AND THE PROCESS-WIDE HOOK IS NOT REACHED when the per-session one declines —
    /// otherwise the refusal would be advisory and the wrong session rebuilt anyway.</summary>
    [Fact]
    public void ARefusalDoesNotFallBackToTheProcessWideHook()
    {
        using var manager = Manager();
        var session = new Session(_dir);

        var fellBack = false;
        manager.RewireOne = _ => false;
        manager.Rewire = () => fellBack = true;

        manager.Resume(session, Snapshot());

        Assert.False(fellBack);
    }

    /// <summary>A HOST THAT SETS NEITHER STILL GETS THE OLD BEHAVIOUR — an embedder with one session
    /// has nothing to disambiguate and should not have to supply a hook to keep working.</summary>
    [Fact]
    public void TheProcessWideHookStillWorksOnItsOwn()
    {
        using var manager = Manager();
        var session = new Session(_dir);

        var ran = false;
        manager.Rewire = () => ran = true;

        manager.Resume(session, Snapshot());

        Assert.True(ran);
    }
}

