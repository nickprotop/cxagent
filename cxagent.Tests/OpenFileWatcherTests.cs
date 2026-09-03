using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

[Collection("file-tabs")]
public class OpenFileWatcherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-watch").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RaisesOnlyForFilesThatAreOpen()
    {
        var open = new OpenFiles();
        var watched = Path.Combine(_dir, "open.cs");
        open.Add(watched);
        var seen = new List<string>();

        using var w = new OpenFileWatcher(_dir, open, p => seen.Add(p));
        w.RaiseForTest(watched);
        w.RaiseForTest(Path.Combine(_dir, "not-open.cs"));

        Assert.Single(seen);
        Assert.Equal(Path.GetFullPath(watched), seen[0]);
    }

    // OUR OWN SAVE IS NOT AN EXTERNAL CHANGE. Without this every save announces itself back and the
    // tab reports a conflict with itself.
    [Fact]
    public void IsSilentWhileASaveIsInFlight()
    {
        var open = new OpenFiles();
        var path = Path.Combine(_dir, "a.cs");
        open.Add(path);
        var seen = new List<string>();

        using var w = new OpenFileWatcher(_dir, open, p => seen.Add(p));
        FileTab.SuppressWatch = true;
        try { w.RaiseForTest(path); }
        finally { FileTab.SuppressWatch = false; }

        Assert.Empty(seen);
    }

    // THE FLAG IS CLEARED AGAIN, or the watcher would be silent for the rest of the session with
    // nothing to say why.
    [Fact]
    public void ResumesAfterTheSaveIsDone()
    {
        var open = new OpenFiles();
        var path = Path.Combine(_dir, "b.cs");
        open.Add(path);
        var seen = new List<string>();

        using var w = new OpenFileWatcher(_dir, open, p => seen.Add(p));
        FileTab.SuppressWatch = true;
        FileTab.SuppressWatch = false;
        w.RaiseForTest(path);

        Assert.Single(seen);
    }
}
