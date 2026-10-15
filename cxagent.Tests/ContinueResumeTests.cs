using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>-c is Claude's "most recent conversation": finished counts, continued does not.</summary>
public class ContinueResumeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cont-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;

    public ContinueResumeTests()
    {
        _work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(_work);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    private FolderSessionStore Store() => new(new AppPaths(_dir));

    private static void Save(FolderSessionStore store, string id, string workingDir) =>
        store.SaveTurn(new FolderSessionStore.ResumeTurn(id, [new ChatMessage { Role = "user", Content = id }],
            0, 0, workingDir, null));

    [Fact]
    public void AFinishedSession_IsStillTheOneToContinue()
    {
        var store = Store();
        Save(store, "AAAAAAAAAAAAAAAAAAAAAAAAA1", _work);
        store.MarkFinished("AAAAAAAAAAAAAAAAAAAAAAAAA1");

        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAA1", store.LoadLatestUncontinued(_work)?.AgentId);
    }

    [Fact]
    public void ASupersededSession_IsSkipped()
    {
        var store = Store();
        Save(store, "AAAAAAAAAAAAAAAAAAAAAAAAA1", _work);
        Thread.Sleep(20);
        Save(store, "AAAAAAAAAAAAAAAAAAAAAAAAA2", _work);
        store.MarkSuperseded("AAAAAAAAAAAAAAAAAAAAAAAAA2");

        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAA1", store.LoadLatestUncontinued(_work)?.AgentId);
    }

    [Fact]
    public void Continue_WithNothingHere_SaysSo()
    {
        var target = Startup.FindResumeTarget(Store(), _work, ResumeRequest.Continue);

        Assert.Null(target.Snapshot);
        Assert.Contains("nothing to continue", target.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pick_FindsNothing_ByDesign()
    {
        Save(Store(), "AAAAAAAAAAAAAAAAAAAAAAAAA1", _work);

        var target = Startup.FindResumeTarget(Store(), _work, ResumeRequest.Pick);

        Assert.Null(target.Snapshot);
        Assert.Null(target.Problem);
    }
}
