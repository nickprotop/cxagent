using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class OpenFilesTests
{
    [Fact]
    public void Add_ReturnsTheFileNameAsTheTitle()
    {
        var open = new OpenFiles();
        Assert.Equal("Program.cs", open.Add(Path.Combine(Path.GetTempPath(), "x", "Program.cs")));
    }

    // TWO FILES, ONE NAME. A bare file name would make the second tab indistinguishable from the
    // first, and TabControl finds tabs by title.
    [Fact]
    public void Add_DisambiguatesASecondFileWithTheSameName()
    {
        var open = new OpenFiles();
        open.Add(Path.Combine(Path.GetTempPath(), "a", "Program.cs"));

        var second = open.Add(Path.Combine(Path.GetTempPath(), "b", "Program.cs"));

        Assert.NotEqual("Program.cs", second);
        Assert.Contains("Program.cs", second);
    }

    [Fact]
    public void TryGetTitle_MatchesRegardlessOfPathSpelling()
    {
        var open = new OpenFiles();
        var title = open.Add(Path.Combine(Path.GetTempPath(), "z.cs"));

        var found = open.TryGetTitle(Path.Combine(Path.GetTempPath(), ".", "z.cs"), out var got);

        Assert.True(found);
        Assert.Equal(title, got);
    }

    // A SECOND OPEN FINDS THE FIRST TAB rather than making a second one over the same bytes.
    [Fact]
    public void Add_Twice_ReturnsTheSameTitle()
    {
        var open = new OpenFiles();
        var path = Path.Combine(Path.GetTempPath(), "same.cs");

        Assert.Equal(open.Add(path), open.Add(path));
    }

    [Fact]
    public void Remove_ForgetsIt()
    {
        var open = new OpenFiles();
        var path = Path.Combine(Path.GetTempPath(), "x", "a.cs");
        open.Add(path);

        open.Remove(path);

        Assert.False(open.TryGetTitle(path, out _));
    }

    [Fact]
    public void PathFor_RoundTripsTheTitle()
    {
        var open = new OpenFiles();
        var path = Path.Combine(Path.GetTempPath(), "x", "a.cs");
        var title = open.Add(path);

        Assert.Equal(Path.GetFullPath(path), open.PathFor(title));
    }
}
