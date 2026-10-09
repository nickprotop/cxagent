using System.Text;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Reading a background command's output while it is still being written — a file nobody promised
/// would hold valid UTF-8, whole lines, or text free of terminal escapes.
/// </summary>
public class OutputTailTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-tail-tests", Guid.NewGuid().ToString("N"));

    public OutputTailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private string Write(string text) => WriteBytes(Encoding.UTF8.GetBytes(text));

    private string WriteBytes(byte[] bytes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".out");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void LastLine_IsTheLastNonBlankLine() =>
        Assert.Equal("tick 3", OutputTail.LastLine(Write("tick 1\ntick 2\ntick 3\n\n\n")));

    [Fact]
    public void LastLine_IsAPartialLineWhenOneIsBeingWritten() =>
        Assert.Equal("compiling", OutputTail.LastLine(Write("done\ncompiling")));

    /// <summary>A progress bar redraws one line with \r; the latest redraw is its state.</summary>
    [Fact]
    public void LastLine_IsTheLatestRedrawOfAProgressBar() =>
        Assert.Equal("90%", OutputTail.LastLine(Write("start\n10%\r50%\r90%\r")));

    [Fact]
    public void LastLine_StripsAnsiAndControlCharacters() =>
        Assert.Equal("ok done", OutputTail.LastLine(Write("\u001b[32mok\u001b[0m\u0007 done\n")));

    [Fact]
    public void LastLine_IsNull_ForAMissingOrEmptyFile()
    {
        Assert.Null(OutputTail.LastLine(Path.Combine(_dir, "nope")));
        Assert.Null(OutputTail.LastLine(Write("")));
        Assert.Null(OutputTail.LastLine(null));
    }

    /// <summary>The jobs panel shows a job's last few lines, oldest first, blank lines skipped.</summary>
    [Fact]
    public void LastLines_AreTheLastNonBlankLines_OldestFirst()
    {
        var path = Write("one\ntwo\n\nthree\nfour\n\n");

        Assert.Equal(["two", "three", "four"], OutputTail.LastLines(path, 3));
        Assert.Equal(["one", "two", "three", "four"], OutputTail.LastLines(path, 10));
        Assert.Empty(OutputTail.LastLines(Path.Combine(_dir, "nope"), 3));
    }

    /// <summary>Only the tail is read: a gigabyte log costs the panel four kilobytes a second.</summary>
    [Fact]
    public void LastLine_ReadsOnlyTheTail() =>
        Assert.Equal("last", OutputTail.LastLine(Write(new string('x', 100_000) + "\nlast\n")));

    /// <summary>Escaping is the renderer's job; doing it here would paste backslashes elsewhere.</summary>
    [Fact]
    public void Clean_KeepsMarkupCharactersForTheCallerToEscape() =>
        Assert.Equal("[red]x[/]", OutputTail.Clean("[red]x[/]"));

    /// <summary>The output file starts with a byte-order mark; a job with no output yet is not "\uFEFF".</summary>
    [Fact]
    public void LastLine_IgnoresTheByteOrderMark()
    {
        Assert.Null(OutputTail.LastLine(WriteBytes([0xEF, 0xBB, 0xBF])));
        Assert.Equal("first", OutputTail.LastLine(WriteBytes([0xEF, 0xBB, 0xBF, (byte)'f', (byte)'i',
            (byte)'r', (byte)'s', (byte)'t', (byte)'\n'])));
    }

    [Fact]
    public void Clean_StripsOscTitleSequences() =>
        Assert.Equal("text", OutputTail.Clean("\u001b]0;title\u0007text"));

    /// <summary>A model's multi-line command is one row in a panel, a picker, and a tab title.</summary>
    [Fact]
    public void OneLine_IsTheFirstLine_MarkedWhenMoreFollow()
    {
        Assert.Equal("for i in 1 2; do …", OutputTail.OneLine("\nfor i in 1 2; do\n  echo $i\ndone"));
        Assert.Equal("sleep 40", OutputTail.OneLine("sleep 40"));
    }

    [Fact]
    public void Follower_ReadsWhatWasAppended_AndNothingTwice()
    {
        var path = Write("one\n");
        var follower = new OutputFollower(path);

        Assert.Equal("one\n", follower.ReadNew());
        File.AppendAllText(path, "two\n");
        Assert.Equal("two\n", follower.ReadNew());
        Assert.Equal("", follower.ReadNew());
    }

    /// <summary>A read that splits a multi-byte character must not turn it into replacement marks.</summary>
    [Fact]
    public void Follower_CarriesAMultiByteCharacterSplitAcrossReads()
    {
        var euro = Encoding.UTF8.GetBytes("€");   // three bytes
        var path = WriteBytes([(byte)'a', euro[0]]);
        var follower = new OutputFollower(path);

        var first = follower.ReadNew();
        using (var s = new FileStream(path, FileMode.Append)) s.Write(euro, 1, 2);
        var second = follower.ReadNew();

        Assert.Equal("a€", first + second);
    }

    [Fact]
    public void Follower_StartsFromTheTailOfALargeFile()
    {
        var path = Write(new string('x', OutputFollower.MaxInitialBytes + 10) + "\nend\n");
        var follower = new OutputFollower(path);

        var text = follower.ReadNew()!;

        Assert.True(follower.StartedFromTail);
        Assert.EndsWith("end\n", text);
        Assert.True(text.Length <= OutputFollower.MaxInitialBytes);
    }

    /// <summary>A file rewritten shorter is read again from its start, not ignored forever.</summary>
    [Fact]
    public void Follower_StartsAgain_WhenTheFileShrinks()
    {
        var path = Write("a long first version\n");
        var follower = new OutputFollower(path);
        follower.ReadNew();

        File.WriteAllText(path, "new\n");

        Assert.Equal("new\n", follower.ReadNew());
    }

    [Fact]
    public void Follower_IsNull_WhenTheFileIsGone() =>
        Assert.Null(new OutputFollower(Path.Combine(_dir, "gone")).ReadNew());

    [Fact]
    public void Lines_JoinAPartialLineAcrossChunks_AndShowTheLatestRedraw()
    {
        var lines = new OutputLines();
        lines.Append("buil");
        lines.Append("ding\n10%\r");
        lines.Append("90%\nnext");

        Assert.Equal(["building", "90%", "next"], lines.Display());
    }

    [Fact]
    public void Lines_DropTheOldestPastTheCap()
    {
        var lines = new OutputLines();
        lines.Append(string.Concat(Enumerable.Range(0, OutputLines.MaxLines + 10).Select(i => $"l{i}\n")));

        var shown = lines.Display();
        Assert.Equal(OutputLines.MaxLines, shown.Count);
        Assert.Equal($"l{OutputLines.MaxLines + 9}", shown[^1]);
    }
}
