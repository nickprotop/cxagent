using System.Text;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Builtin;
using Xunit;

namespace CxAgent.Tests;

public class FileJobPluginTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-file-" + Guid.NewGuid().ToString("N"));
    public FileJobPluginTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static JobParameters P(params (string k, object? v)[] kv)
        => new(kv.ToDictionary(x => x.k, x => x.v));
    private static readonly CollectingContext Ctx = new();

    [Fact]
    public async Task Write_ThenRead_RoundTripsContent()
    {
        var path = Path.Combine(_dir, "f.txt");
        var write = await new FileJobPlugin().ExecuteAsync(
            P(("action", "write"), ("path", path), ("content", "hello file")), Ctx, CancellationToken.None);
        Assert.True(write.Success);

        var read = await new FileJobPlugin().ExecuteAsync(
            P(("action", "read"), ("path", path)), Ctx, CancellationToken.None);
        Assert.True(read.Success);
        Assert.Equal("hello file", (string)read.Output["content"]!);
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        var path = Path.Combine(_dir, "d.txt");
        await File.WriteAllTextAsync(path, "x");
        var r = await new FileJobPlugin().ExecuteAsync(
            P(("action", "delete"), ("path", path)), Ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Validate_RejectsMissingPath()
    {
        var v = new FileJobPlugin().Validate(P(("action", "read")));
        Assert.False(v.IsValid);
    }

    [Fact]
    public void Validate_RejectsWriteWithoutContent()
    {
        var v = new FileJobPlugin().Validate(P(("action", "write"), ("path", "/tmp/x")));
        Assert.False(v.IsValid);
    }

    [Fact]
    public void Validate_RejectsCopyWithoutDest()
    {
        var v = new FileJobPlugin().Validate(P(("action", "copy"), ("path", "/tmp/x")));
        Assert.False(v.IsValid);
    }

    [Fact]
    public void TypeName_IsFile()
    {
        Assert.Equal("file", new FileJobPlugin().TypeName);
    }

    private async Task<JobResult> Read(string path, params (string k, object? v)[] extra)
        => await new FileJobPlugin().ExecuteAsync(
            P(new[] { ("action", (object?)"read"), ("path", path) }.Concat(extra).ToArray()),
            Ctx, CancellationToken.None);

    private string TenLineFile()
    {
        var path = Path.Combine(_dir, "ten.txt");
        File.WriteAllText(path, string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line{i}")));
        return path;
    }

    [Fact]
    public async Task Read_WithOffsetAndLimit_ReturnsThatWindow()
    {
        var r = await Read(TenLineFile(), ("offset", 3), ("limit", 2));
        Assert.Equal("line3\nline4", (string)r.Output["content"]!);
    }

    [Fact]
    public async Task Read_PagingTwice_ReturnsDIFFERENTContent()
    {
        // THE REGRESSION. A worker asked for a 36KB file, got a head+tail elision, had no way to
        // ask for the missing middle, and re-issued the SAME call until the turn cap killed it.
        // Two successive windows returning different text is the property that breaks that loop.
        var path = TenLineFile();
        var first = (string)(await Read(path, ("offset", 1), ("limit", 3))).Output["content"]!;
        var second = (string)(await Read(path, ("offset", 4), ("limit", 3))).Output["content"]!;

        Assert.NotEqual(first, second);
        Assert.Equal("line1\nline2\nline3", first);
        Assert.Equal("line4\nline5\nline6", second);
    }

    [Fact]
    public async Task Read_ReportsTotalLines_SoAWorkerCanComputeTheNextWindow()
    {
        // Without this the tool is still a loop: the model cannot tell whether it is a tenth of the
        // way through the file or already past the end.
        Assert.Equal(10, (int)(await Read(TenLineFile(), ("offset", 1), ("limit", 3))).Output["total_lines"]!);
        Assert.Equal(10, (int)(await Read(TenLineFile())).Output["total_lines"]!);
    }

    [Fact]
    public async Task Read_PastEndOfFile_IsEmptyNotAnError()
    {
        // A model that overshoots has made an off-by-one, not a fatal error. An empty window plus
        // total_lines is precisely the signal to stop paging; an error string burns a whole turn.
        var r = await Read(TenLineFile(), ("offset", 500), ("limit", 10));
        Assert.True(r.Success);
        Assert.Equal("", (string)r.Output["content"]!);
        Assert.Equal(10, (int)r.Output["total_lines"]!);
    }

    [Fact]
    public async Task Read_ClampsAZeroOffset_RatherThanThrowing()
    {
        var r = await Read(TenLineFile(), ("offset", 0), ("limit", 2));
        Assert.True(r.Success);
        Assert.Equal("line1\nline2", (string)r.Output["content"]!);
    }

    [Fact]
    public async Task Read_WholeFile_StillReturnsExactBytes()
    {
        // The windowed path joins on '\n'; the unwindowed path must NOT, or every whole-file read
        // would silently rewrite line endings for callers who never asked for a window.
        var path = Path.Combine(_dir, "crlf.txt");
        File.WriteAllText(path, "a\r\nb\r\n");
        Assert.Equal("a\r\nb\r\n", (string)(await Read(path)).Output["content"]!);
    }

    // --- list / search / replace ------------------------------------------------------------------

    private async Task<JobResult> Run(params (string k, object? v)[] kv) =>
        await new FileJobPlugin().ExecuteAsync(P(kv), Ctx, CancellationToken.None);

    [Fact]
    public async Task List_FindsFilesWithoutAShellCommand()
    {
        // Through run_shell this is `find`, which raises a permission prompt for an operation that
        // reads nothing the role could not already read. Live drives stalled repeatedly on exactly
        // those approvals, and a worker blocked on a prompt is a worker doing nothing.
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "y");

        var r = await Run(("action", "list"), ("path", _dir), ("pattern", "*.cs"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("a.cs", (string)r.Output["content"]!);
        Assert.DoesNotContain("b.txt", (string)r.Output["content"]!);
    }

    [Fact]
    public async Task List_OnAFilePath_ListsItsDirectory()
    {
        // A model that lists "src/Foo.cs" meant "the folder Foo.cs is in". Erroring teaches it
        // nothing it can act on.
        var f = Path.Combine(_dir, "a.cs");
        File.WriteAllText(f, "x");

        Assert.True((await Run(("action", "list"), ("path", f))).Success);
    }

    [Fact]
    public async Task Search_ReportsFileLineAndText()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "one\nNEEDLE here\nthree");

        var r = await Run(("action", "search"), ("path", _dir), ("pattern", "NEEDLE"));

        var content = (string)r.Output["content"]!;
        Assert.Contains("a.cs", content);
        Assert.Contains(":2:", content);              // the LINE, so the model can go straight there
        Assert.Contains("NEEDLE here", content);
    }

    [Fact]
    public async Task Search_SurvivesAnUnreadableFile()
    {
        // A binary or locked file is not an error worth failing the whole search over.
        File.WriteAllBytes(Path.Combine(_dir, "bin.dat"), new byte[] { 0xFF, 0xFE, 0x00 });
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "NEEDLE");

        var r = await Run(("action", "search"), ("path", _dir), ("pattern", "NEEDLE"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("a.cs", (string)r.Output["content"]!);
    }

    [Fact]
    public async Task Replace_ChangesOneOccurrence_WithoutRewritingTheFile()
    {
        // `write` is whole-file only, so changing one function in a 500-line file meant reproducing
        // all 500 from memory -- every unchanged line a chance to alter something silently.
        var f = Path.Combine(_dir, "a.cs");
        File.WriteAllText(f, "line1\nTARGET\nline3");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "TARGET"), ("replacement", "FIXED"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("line1\nFIXED\nline3", File.ReadAllText(f));
    }

    [Fact]
    public async Task Replace_RefusesAnAMBIGUOUSMatch_AndWritesNOTHING()
    {
        // Picking one silently is how the wrong line gets changed in a file nobody is watching.
        var f = Path.Combine(_dir, "a.cs");
        File.WriteAllText(f, "DUP\nmiddle\nDUP");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "DUP"), ("replacement", "X"));

        Assert.False(r.Success);
        Assert.Contains("ambiguous", r.ErrorMessage!);
        Assert.Equal("DUP\nmiddle\nDUP", File.ReadAllText(f));   // untouched
    }

    [Fact]
    public async Task Replace_SaysToCopyTheExactText_WhenNothingMatches()
    {
        // Zero matches usually means the model is editing from memory rather than from the file.
        var f = Path.Combine(_dir, "a.cs");
        File.WriteAllText(f, "actual content");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "imagined"), ("replacement", "X"));

        Assert.False(r.Success);
        Assert.Contains("from memory", r.ErrorMessage!);
    }

    [Fact]
    public async Task Search_SupportsRegex_WhenAskedFor()
    {
        // "TODO|FIXME" and "class \\w+Decoder" are ordinary questions. Literal-only forces several
        // round trips to answer one of them, and each is a paid turn.
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "class HexDecoder\nclass Other\nclass UUDecoder");

        var r = await Run(("action", "search"), ("path", _dir),
                          ("pattern", @"class \w+Decoder"), ("regex", true));

        var content = (string)r.Output["content"]!;
        Assert.Contains("HexDecoder", content);
        Assert.Contains("UUDecoder", content);
        Assert.DoesNotContain("class Other", content);
    }

    [Fact]
    public async Task Search_IsLITERALByDefault()
    {
        // Opt-in, because a pattern containing . or ( means something different under each mode.
        // Silently choosing regex would change what an existing literal search finds.
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "a.b\naxb");

        var r = await Run(("action", "search"), ("path", _dir), ("pattern", "a.b"));

        var content = (string)r.Output["content"]!;
        Assert.Contains("a.b", content);
        Assert.DoesNotContain("axb", content);   // '.' was not a wildcard
    }

    [Fact]
    public async Task Search_AnInvalidRegex_SaysHowToFixIt()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "x");

        var r = await Run(("action", "search"), ("path", _dir),
                          ("pattern", "class ((("), ("regex", true));

        Assert.False(r.Success);
        Assert.Contains("Omit `regex`", r.ErrorMessage!);
    }

    [Fact]
    public async Task Search_CanRestrictToAGlob()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "NEEDLE");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "NEEDLE");

        var r = await Run(("action", "search"), ("path", _dir),
                          ("pattern", "NEEDLE"), ("glob", "*.cs"));

        var content = (string)r.Output["content"]!;
        Assert.Contains("a.cs", content);
        Assert.DoesNotContain("b.txt", content);
    }

    [Fact]
    public async Task Read_OnADirectory_SaysToUseListInstead()
    {
        // File.ReadAllTextAsync throws "Access to the path ... is denied", which reads as a
        // PERMISSIONS problem -- and a model that believes it lacks access does not retry with the
        // right action, it hunts for another route in. Seen live: ten consecutive discovery jobs,
        // two of them this exact failure, and the goal never reached the edit it was asked for.
        var r = await Run(("action", "read"), ("path", _dir));

        Assert.False(r.Success);
        Assert.Contains("is a directory", r.ErrorMessage!);
        Assert.Contains("list", r.ErrorMessage!);
        Assert.DoesNotContain("denied", r.ErrorMessage!);
    }

    [Fact]
    public async Task Replace_PreservesAUtf8Bom()
    {
        // Caught live: HexEncoder.cs went from EF BB BF to 2F 2F on a two-line insertion, because
        // File.WriteAllTextAsync writes UTF-8 without a BOM regardless of what the file had. In a C#
        // repo that is a spurious diff on every file an implementer touches -- and the kind of
        // change nobody attributes to the agent that made it.
        var f = Path.Combine(_dir, "bom.cs");
        File.WriteAllText(f, "line1\nTARGET\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "TARGET"), ("replacement", "FIXED"));

        Assert.True(r.Success, r.ErrorMessage);
        var head = File.ReadAllBytes(f).Take(3).ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, head);
        Assert.Contains("FIXED", File.ReadAllText(f));
    }

    [Fact]
    public async Task Replace_DoesNotAddABomToAFileWithoutOne()
    {
        // The converse: adding a BOM to a file that had none is the same spurious diff in reverse.
        var f = Path.Combine(_dir, "nobom.cs");
        File.WriteAllText(f, "line1\nTARGET\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await Run(("action", "replace"), ("path", f), ("pattern", "TARGET"), ("replacement", "FIXED"));

        Assert.NotEqual(0xEF, File.ReadAllBytes(f)[0]);
    }

    [Fact]
    public async Task Replace_MatchesWhenOnlyTheINDENTATIONDiffers()
    {
        // Live drive: the orchestrator read HexEncoder.cs, planned a replace, and failed on both
        // files. The source is indented with TABS; the pattern came back with spaces. Asking a model
        // to reproduce indentation exactly from memory defeats the point of `replace`, which exists
        // so it does NOT have to reproduce text from memory.
        var f = Path.Combine(_dir, "tabs.cs");
        File.WriteAllText(f, "class C\n{\n\t\tint M()\n\t\t{\n\t\t\treturn 1;\n\t\t}\n}");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "        return 1;"),          // spaces, not tabs
                          ("replacement", "\t\t\treturn 2;"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("return 2;", File.ReadAllText(f));
        Assert.DoesNotContain("return 1;", File.ReadAllText(f));
    }

    [Fact]
    public async Task Replace_StillRefusesWhenTheTEXTDiffers()
    {
        // Only LEADING whitespace is relaxed. Everything else must still match exactly, or the tool
        // could quietly edit a different piece of code -- the risk that made exact-match the first
        // choice.
        var f = Path.Combine(_dir, "x.cs");
        File.WriteAllText(f, "\t\treturn 1;\n");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "return 999;"), ("replacement", "return 2;"));

        Assert.False(r.Success);
        Assert.Contains("even ignoring indentation", r.ErrorMessage!);
    }

    [Fact]
    public async Task Replace_PreservesTheFilesOwnIndentationAroundTheEdit()
    {
        // The slice replaced is the REAL text from the file, so untouched lines keep their tabs.
        var f = Path.Combine(_dir, "keep.cs");
        File.WriteAllText(f, "\tline1\n\tTARGET\n\tline3\n");

        await Run(("action", "replace"), ("path", f), ("pattern", "TARGET"), ("replacement", "\tFIXED"));

        var after = File.ReadAllText(f);
        Assert.Contains("\tline1", after);
        Assert.Contains("\tline3", after);
    }

    [Fact]
    public async Task Replace_MatchesAcrossHOUSESTYLESpacing()
    {
        // The second live failure, after indentation was relaxed and the tool still said "not found
        // even ignoring indentation". MimeKit writes `EstimateOutputLength (int inputLength)` with a
        // space before the paren; standard C# does not, and a model writes standard C#. That is not
        // something it can know without having looked at the exact bytes -- which is what `replace`
        // exists to spare it.
        var f = Path.Combine(_dir, "style.cs");
        File.WriteAllText(f, "\t\tpublic int Estimate (int n)\n\t\t{\n\t\t\treturn n * 3;\n\t\t}\n");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "public int Estimate(int n)"),      // no space before paren
                          ("replacement", "\t\tpublic int Estimate (long n)"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("long n", File.ReadAllText(f));
    }

    [Fact]
    public async Task Replace_StillRefusesADIFFERENTIdentifier()
    {
        // Whitespace only. Every non-space character must still match in order, or the tool could
        // quietly edit a different piece of code.
        var f = Path.Combine(_dir, "ident.cs");
        File.WriteAllText(f, "\t\treturn alpha * 3;\n");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "return beta * 3;"), ("replacement", "x"));

        Assert.False(r.Success);
    }
}
