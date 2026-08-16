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

    // THE BUG REPLACE ALREADY FIXED, through the other door. ReplaceAsync preserves a UTF-8 BOM
    // because a live drive turned HexEncoder.cs from EF BB BF into 2F 2F on a two-line insertion;
    // write_file stripped it again, so which tool the model happened to pick decided whether the
    // repo picked up a spurious diff.
    [Fact]
    public async Task Write_KeepsTheBomAFileAlreadyHad()
    {
        var path = Path.Combine(_dir, "bom.cs");
        await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, .. "old"u8.ToArray()]);

        var r = await Run(("action", "write"), ("path", path), ("content", "new content"));

        Assert.True(r.Success, r.ErrorMessage);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("new content", await File.ReadAllTextAsync(path));
    }

    // AND DOES NOT ADD ONE. A file without a BOM must not acquire one, or the same spurious diff
    // appears from the opposite direction.
    [Fact]
    public async Task Write_DoesNotAddABomToAFileThatHadNone()
    {
        var path = Path.Combine(_dir, "plain.cs");
        await File.WriteAllTextAsync(path, "old");

        await Run(("action", "write"), ("path", path), ("content", "new"));

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public async Task Write_ANewFileGetsNoBom()
    {
        var path = Path.Combine(_dir, "fresh.cs");

        await Run(("action", "write"), ("path", path), ("content", "hello"));

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.NotEqual(0xEF, bytes[0]);
    }

    // APPEND MUST NEVER INSERT A BOM MID-FILE, which is what encoding one onto an append stream
    // would do — the marker only means anything as the first three bytes.
    [Fact]
    public async Task Append_NeverWritesABomIntoTheMiddle()
    {
        var path = Path.Combine(_dir, "bom-append.cs");
        await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, .. "first\n"u8.ToArray()]);

        await Run(("action", "append"), ("path", path), ("content", "second\n"));

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);

        // EXACTLY ONE, at the front. The marker means nothing anywhere else, so a second copy
        // between the two writes would be corruption that still starts with the right three bytes.
        var occurrences = bytes.Where((_, i) => i + 2 < bytes.Length
            && bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF).Count();
        Assert.Equal(1, occurrences);
        Assert.EndsWith("first\nsecond\n", System.Text.Encoding.UTF8.GetString(bytes));
    }

    // CREATED AND OVERWROTE ARE DIFFERENT EVENTS. An agent that meant to create a file and silently
    // replaced one had nothing in the result to notice it by.
    [Fact]
    public async Task Write_SaysWhetherItCreatedOrOverwrote()
    {
        var path = Path.Combine(_dir, "twice.txt");

        var first = await Run(("action", "write"), ("path", path), ("content", "one"));
        Assert.True((bool)first.Output["created"]!);
        Assert.Contains("created", (string)first.Output["content"]!);

        var second = await Run(("action", "write"), ("path", path), ("content", "two"));
        Assert.False((bool)second.Output["created"]!);
        Assert.Contains("overwrote", (string)second.Output["content"]!);
    }

    // A CRLF FILE MUST STAY CRLF. The model sends \n (it cannot see line endings in a tool result,
    // and reproduces what it read), so a multi-line replacement splices LF into a CRLF file and
    // leaves it mixed — the same class of invisible, unattributable diff as the BOM.
    [Fact]
    public async Task Replace_KeepsTheFilesOwnLineEndings()
    {
        var path = Path.Combine(_dir, "crlf.cs");
        await File.WriteAllTextAsync(path, "class A\r\n{\r\n    void Old()\r\n    {\r\n    }\r\n}\r\n");

        var r = await Run(("action", "replace"), ("path", path),
            ("pattern", "    void Old()\n    {\n    }"),
            ("replacement", "    void New()\n    {\n        Go();\n    }"));

        Assert.True(r.Success, r.ErrorMessage);
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("void New()", text);
        // No bare LF anywhere: every \n must be preceded by \r.
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public async Task Write_KeepsTheFilesOwnLineEndings()
    {
        var path = Path.Combine(_dir, "crlf-write.cs");
        await File.WriteAllTextAsync(path, "old\r\nlines\r\n");

        await Run(("action", "write"), ("path", path), ("content", "new\nlines\nhere\n"));

        var text = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    // A FILE THAT DOES NOT EXIST HAS NO CONVENTION TO KEEP, so its content goes through untouched —
    // inventing CRLF for a new file on a Unix checkout would be its own spurious diff.
    [Fact]
    public async Task Write_ANewFileKeepsTheContentsOwnEndings()
    {
        var path = Path.Combine(_dir, "fresh-lf.cs");

        await Run(("action", "write"), ("path", path), ("content", "a\nb\n"));

        Assert.Equal("a\nb\n", await File.ReadAllTextAsync(path));
    }

    // TWO AGENTS, ONE FILE. cxagent runs sub-agents concurrently and they share one plugin instance
    // through the parent's registry, so a read-modify-write from each can interleave: both read the
    // same text, both match, and the second writes an edit computed from a version that no longer
    // exists — silently, with both reporting success.
    [Fact]
    public async Task Replace_ConcurrentEditsToOneFile_DoNotLoseEachOther()
    {
        var path = Path.Combine(_dir, "shared.cs");
        await File.WriteAllTextAsync(path, "alpha\nbravo\n");

        var plugin = new FileJobPlugin();
        var one = plugin.ExecuteAsync(
            P(("action", "replace"), ("path", path), ("pattern", "alpha"), ("replacement", "ALPHA")),
            Ctx, CancellationToken.None);
        var two = plugin.ExecuteAsync(
            P(("action", "replace"), ("path", path), ("pattern", "bravo"), ("replacement", "BRAVO")),
            Ctx, CancellationToken.None);

        var results = await Task.WhenAll(one, two);
        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));

        // BOTH EDITS SURVIVE. Without the lock one overwrites the other and this file holds exactly
        // one of the two changes.
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("ALPHA", text);
        Assert.Contains("BRAVO", text);
    }

    // Unrelated files must still proceed in parallel: the lock is per path, not one global mutex.
    [Fact]
    public async Task Writes_ToDifferentFiles_AreNotSerialisedAgainstEachOther()
    {
        var plugin = new FileJobPlugin();
        var tasks = Enumerable.Range(0, 8).Select(i => plugin.ExecuteAsync(
            P(("action", "write"), ("path", Path.Combine(_dir, $"p{i}.txt")), ("content", $"{i}")),
            Ctx, CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        for (var i = 0; i < 8; i++)
            Assert.Equal($"{i}", await File.ReadAllTextAsync(Path.Combine(_dir, $"p{i}.txt")));
    }

    // WHITESPACE IS NOT A TARGET, and saying so beats reporting a match count the model never meant.
    // Before this, `replace` with pattern "\n" returned "length ('-1') must be a non-negative value.
    // (Parameter 'length')" — an internal argument name, useless to the caller.
    [Fact]
    public async Task Replace_AWhitespaceOnlyPattern_IsRefusedWithSomethingActionable()
    {
        var path = Path.Combine(_dir, "ws.cs");
        await File.WriteAllTextAsync(path, "class A\n{\n}\n");

        var r = await Run(("action", "replace"), ("path", path),
            ("pattern", "\n"), ("replacement", "\n\n"));

        Assert.False(r.Success);
        Assert.Contains("only whitespace", r.ErrorMessage!);
        Assert.Contains("Nothing was written", r.ErrorMessage!);
        Assert.Equal("class A\n{\n}\n", await File.ReadAllTextAsync(path));
    }

    // IDENTICAL IS NOT AN EDIT. This used to report "replaced 1 occurrence (indentation adjusted to
    // match the file)" — success, with a note implying a change, for an operation that made none.
    [Fact]
    public async Task Replace_WithAnIdenticalReplacement_IsRefused()
    {
        var path = Path.Combine(_dir, "same.cs");
        await File.WriteAllTextAsync(path, "class A\n{\n    void Go() { }\n}\n");

        var r = await Run(("action", "replace"), ("path", path),
            ("pattern", "void Go() { }"), ("replacement", "void Go() { }"));

        Assert.False(r.Success);
        Assert.Contains("identical", r.ErrorMessage!);
        Assert.Contains("Nothing was written", r.ErrorMessage!);
    }

    // THE LOCK IS NOT ENOUGH ON ITS OWN. It serialises agents inside this process; the file can still
    // move underneath an edit for reasons the process cannot see — the user's editor, a formatter, a
    // git checkout. Applying an edit computed from content that no longer exists succeeds silently
    // and reverts whatever happened in between.
    [Fact]
    public async Task WriteIfUnchanged_RefusesAnEditComputedFromContentThatMoved()
    {
        var path = Path.Combine(_dir, "moved.cs");
        await File.WriteAllTextAsync(path, "original\n");

        var read = await FileMutation.ReadAsync(path, CancellationToken.None);

        // Something else edits it between the read and the write.
        await File.WriteAllTextAsync(path, "someone else's work\n");

        await Assert.ThrowsAsync<StaleContentException>(() =>
            FileMutation.WriteIfUnchangedAsync(path, "my edit\n", read, CancellationToken.None));

        // AND NOTHING WAS WRITTEN: the other change survives intact.
        Assert.Equal("someone else's work\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteIfUnchanged_WritesWhenTheFileIsStillWhatWasRead()
    {
        var path = Path.Combine(_dir, "stable.cs");
        await File.WriteAllTextAsync(path, "original\n");

        var read = await FileMutation.ReadAsync(path, CancellationToken.None);
        await FileMutation.WriteIfUnchangedAsync(path, "edited\n", read, CancellationToken.None);

        Assert.Equal("edited\n", await File.ReadAllTextAsync(path));
    }

    // The message has to tell the model what to DO. "Read it again and redo the edit" is actionable;
    // a bare "stale content" is not.
    [Fact]
    public async Task StaleContent_SaysWhatToDoAboutIt()
    {
        var path = Path.Combine(_dir, "msg.cs");
        await File.WriteAllTextAsync(path, "one\n");
        var read = await FileMutation.ReadAsync(path, CancellationToken.None);
        await File.WriteAllTextAsync(path, "two\n");

        var ex = await Assert.ThrowsAsync<StaleContentException>(() =>
            FileMutation.WriteIfUnchangedAsync(path, "three\n", read, CancellationToken.None));

        Assert.Contains("changed on disk", ex.Message);
        Assert.Contains("Nothing was written", ex.Message);
        Assert.Contains("Read the file again", ex.Message);
    }

    // AMBIGUITY NOW SAYS WHERE. It said "appears 3 times" and stopped, leaving the model to invent
    // three disambiguating patterns against a file it had to re-read. This is also the answer to
    // replaceAll, declined deliberately: showing the sites keeps each edit individually aimed and
    // reviewable while removing most of what made refusing expensive.
    [Fact]
    public async Task Replace_AnAmbiguousPattern_SaysWhereItMatched()
    {
        var path = Path.Combine(_dir, "many.cs");
        await File.WriteAllTextAsync(path,
            "class A\n{\n    int count;\n    void Go() { count++; }\n    void Stop() { count--; }\n}\n");

        var r = await Run(("action", "replace"), ("path", path),
            ("pattern", "count"), ("replacement", "total"));

        Assert.False(r.Success);
        Assert.Contains("appears 3 times", r.ErrorMessage!);
        Assert.Contains("It matches at:", r.ErrorMessage!);
        Assert.Contains("line 3:", r.ErrorMessage!);
        Assert.Contains("line 5:", r.ErrorMessage!);
        Assert.Contains("Nothing was written", r.ErrorMessage!);
    }

    // CAPPED, because the point is to let the model AIM rather than to reproduce the file: a pattern
    // matching forty times is one to reconsider, not to disambiguate.
    [Fact]
    public async Task Replace_ManyMatches_ShowsAFewAndCountsTheRest()
    {
        var path = Path.Combine(_dir, "lots.cs");
        await File.WriteAllTextAsync(path,
            string.Concat(Enumerable.Range(0, 9).Select(i => $"var x{i} = same;\n")));

        var r = await Run(("action", "replace"), ("path", path),
            ("pattern", "same"), ("replacement", "other"));

        Assert.False(r.Success);
        Assert.Contains("and 4 more", r.ErrorMessage!);
    }

    // CREATE MEANS NEW, and says so rather than replacing. write_file cannot tell "make me a new
    // file" from "make the file say this" — it reports which it did, afterwards, which is useful and
    // too late for a caller who stated an expectation.
    [Fact]
    public async Task Create_MakesAFileThatDoesNotExist()
    {
        var path = Path.Combine(_dir, "new.md");

        var r = await Run(("action", "create"), ("path", path), ("content", "# plan"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("# plan", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Create_RefusesToReplaceAnExistingFile()
    {
        var path = Path.Combine(_dir, "taken.md");
        await File.WriteAllTextAsync(path, "somebody's work");

        var r = await Run(("action", "create"), ("path", path), ("content", "mine"));

        Assert.False(r.Success);
        Assert.Contains("already exists", r.ErrorMessage!);
        Assert.Contains("Nothing was written", r.ErrorMessage!);
        Assert.Equal("somebody's work", await File.ReadAllTextAsync(path));
    }

    // ATOMIC: the check and the create are one syscall, so of N concurrent creates exactly one wins.
    // An Exists() test followed by a write lets several through — the shape a second process breaks.
    [Fact]
    public async Task Create_ConcurrentAttempts_ExactlyOneWins()
    {
        var path = Path.Combine(_dir, "race.md");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            FileMutation.CreateNewAsync(path, $"writer {i}", CancellationToken.None)));

        Assert.Equal(1, results.Count(won => won));
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

    // BUILD OUTPUT IS NOT THE PROJECT. A live explorer fell back to `ls -R`, met bin/ and obj/
    // first, and reported to its planner that cxgpu consists of DLLs — which was true of what it
    // had seen. list/search drop what the REPOSITORY says is ignored, asked of git rather than
    // guessed from a list of names: the names that matter differ per project (build/, out/,
    // vendor/, .next/), and a repo that commits dist/ on purpose must not have it hidden.
    [Fact]
    public async Task List_SkipsWhatTheRepositoryIgnores()
    {
        InitRepo(".gitignore", "obj/\nnode_modules/\n");
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "obj", "Debug"));
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_dir, "src", "Real.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "obj", "Debug", "Generated.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "node_modules", "pkg", "Vendored.cs"), "x");

        var r = await Run(("action", "list"), ("path", _dir), ("pattern", "**/*.cs"));

        Assert.True(r.Success, r.ErrorMessage);
        var content = (string)r.Output["content"]!;
        Assert.Contains("Real.cs", content);
        Assert.DoesNotContain("Generated.cs", content);
        Assert.DoesNotContain("Vendored.cs", content);
    }

    // THE CASE A HARDCODED LIST GETS WRONG. "dist" is a conventional build directory, so a name
    // list would hide it — but this repo commits it, and git says so. The repo's opinion wins.
    [Fact]
    public async Task List_KeepsConventionallyGeneratedNamesTheRepositoryDoesNotIgnore()
    {
        InitRepo(".gitignore", "obj/\n");
        Directory.CreateDirectory(Path.Combine(_dir, "dist"));
        File.WriteAllText(Path.Combine(_dir, "dist", "Shipped.cs"), "x");

        var r = await Run(("action", "list"), ("path", _dir), ("pattern", "**/*.cs"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("Shipped.cs", (string)r.Output["content"]!);
    }

    // AND THE CASE IT MISSES: a name nobody would think to hardcode, ignored by this project.
    [Fact]
    public async Task List_SkipsAProjectSpecificIgnoredDirectory()
    {
        InitRepo(".gitignore", "generated-sources/\n");
        Directory.CreateDirectory(Path.Combine(_dir, "generated-sources"));
        File.WriteAllText(Path.Combine(_dir, "generated-sources", "Gen.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "Real.cs"), "x");

        var r = await Run(("action", "list"), ("path", _dir), ("pattern", "**/*.cs"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("Real.cs", (string)r.Output["content"]!);
        Assert.DoesNotContain("Gen.cs", (string)r.Output["content"]!);
    }

    // NO REPO, NO FILTERING. There is no authority to consult outside a checkout, so a caller who
    // asked to list a directory gets that directory rather than a guess about it.
    [Fact]
    public async Task List_OutsideAGitRepository_FiltersNothing()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "obj"));
        File.WriteAllText(Path.Combine(_dir, "obj", "Generated.cs"), "x");

        var r = await Run(("action", "list"), ("path", _dir), ("pattern", "**/*.cs"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("Generated.cs", (string)r.Output["content"]!);
    }

    [Fact]
    public async Task Search_SkipsWhatTheRepositoryIgnores()
    {
        InitRepo(".gitignore", "obj/\n");
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "obj"));
        File.WriteAllText(Path.Combine(_dir, "src", "Real.cs"), "needle here");
        File.WriteAllText(Path.Combine(_dir, "obj", "Generated.cs"), "needle here");

        var r = await Run(("action", "search"), ("path", _dir), ("pattern", "needle"));

        Assert.True(r.Success, r.ErrorMessage);
        var content = (string)r.Output["content"]!;
        Assert.Contains("Real.cs", content);
        Assert.DoesNotContain("Generated.cs", content);
    }

    // THE TEST THAT WAS MISSING, and its absence cost a whole session. Validate accepting a
    // path-less call is not the same as EXECUTING one: the resolution used Get<string>("path"),
    // whose one-argument overload is Values[key] and throws on an absent key. So the schema
    // advertised `path` as optional while every call that believed it came back as "The given key
    // 'path' was not present in the dictionary" — 174 times in one drive. Validate-only tests
    // passed throughout.
    [Fact]
    public async Task List_WithNoPath_SearchesTheWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "x");

        var r = await new FileJobPlugin().ExecuteAsync(
            P(("action", "list"), ("pattern", "*.cs")), new CollectingContext { WorkingDirectory = _dir },
            CancellationToken.None);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("a.cs", (string)r.Output["content"]!);
    }

    [Fact]
    public async Task Search_WithNoPath_SearchesTheWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "needle here");

        var r = await new FileJobPlugin().ExecuteAsync(
            P(("action", "search"), ("pattern", "needle")), new CollectingContext { WorkingDirectory = _dir },
            CancellationToken.None);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("a.cs", (string)r.Output["content"]!);
    }

    // An action that genuinely needs a target still fails, and says which action and why rather
    // than surfacing a dictionary error.
    [Fact]
    public async Task Read_WithNoPath_FailsWithAMessageAboutThePath()
    {
        var r = await new FileJobPlugin().ExecuteAsync(
            P(("action", "read")), new CollectingContext { WorkingDirectory = _dir }, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("'path' is required", r.ErrorMessage!);
    }

    // `path` is optional for the searching actions now that `pattern` is the required one, so a
    // call with no path must validate rather than be rejected before it runs.
    [Fact]
    public void Validate_AcceptsListAndSearchWithNoPath()
    {
        var plugin = new FileJobPlugin();
        Assert.True(plugin.Validate(P(("action", "list"), ("pattern", "*.cs"))).IsValid);
        Assert.True(plugin.Validate(P(("action", "search"), ("pattern", "needle"))).IsValid);
    }

    [Fact]
    public void Validate_StillRequiresPathForActionsThatTargetAFile()
    {
        var plugin = new FileJobPlugin();
        Assert.False(plugin.Validate(P(("action", "read"))).IsValid);
        Assert.False(plugin.Validate(P(("action", "write"), ("content", "x"))).IsValid);
    }

    /// <summary>A real git repo in the test directory, so check-ignore has something to answer
    /// from. Nothing is committed — .gitignore applies to untracked files, which is the case.</summary>
    private void InitRepo(string ignoreFile, string contents)
    {
        File.WriteAllText(Path.Combine(_dir, ignoreFile), contents);
        foreach (var args in new[] { "init -q", "config user.email t@t", "config user.name t" })
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _dir };
            foreach (var a in args.Split(' ')) psi.ArgumentList.Add(a);
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            System.Diagnostics.Process.Start(psi)!.WaitForExit(5000);
        }
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

    // ---- GLOB NORMALISATION -------------------------------------------------
    // `**/*.cs` is what a developer writes and what a model reaches for first. .NET's
    // EnumerateFiles treats `**` as a literal directory name and throws "Could not find a part of
    // the path", which reads as "your path is wrong" rather than "that syntax is unsupported".
    // Seen live on a 1,294-file tree: two failed searches, then a fallback to `find` through
    // run_shell -- a permission prompt for a read the agent was already entitled to make.

    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "class A { }\nTARGET\n");
        File.WriteAllText(Path.Combine(_dir, "sub", "b.cs"), "class B { }\nTARGET\n");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "TARGET\n");
    }

    [Theory]
    [InlineData("**/*.cs")]   // the form that failed live
    [InlineData("*.cs")]      // already worked
    [InlineData("./*.cs")]    // harmless to a shell, fatal to EnumerateFiles
    [InlineData("src/**/*.cs")] // ** mid-pattern: the file half is what selects
    public async Task List_AcceptsEveryStandardGlobForm(string glob)
    {
        Seed();
        var r = await Run(("action", "list"), ("path", _dir), ("pattern", glob));

        Assert.True(r.Success, r.ErrorMessage);
        var content = r.Output["content"] as string ?? "";
        // Recursion is the search mode, so both the root and nested .cs file are found -- and the
        // .txt is excluded, proving the pattern still SELECTS rather than being discarded.
        Assert.Contains("a.cs", content);
        Assert.Contains("b.cs", content);
        Assert.DoesNotContain("notes.txt", content);
    }

    [Theory]
    [InlineData("**/*.cs")]
    [InlineData("*.cs")]
    public async Task Search_AcceptsEveryStandardGlobForm(string glob)
    {
        Seed();
        var r = await Run(("action", "search"), ("path", _dir),
                          ("pattern", "TARGET"), ("glob", glob));

        Assert.True(r.Success, r.ErrorMessage);
        var content = r.Output["content"] as string ?? "";
        Assert.Contains("a.cs", content);
        Assert.Contains("b.cs", content);
        Assert.DoesNotContain("notes.txt", content);   // glob still filters
    }

    [Fact]
    public async Task List_BareDoubleStarMatchesEverything()
    {
        Seed();
        var r = await Run(("action", "list"), ("path", _dir), ("pattern", "**"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("notes.txt", r.Output["content"] as string ?? "");
    }

    [Fact]
    public async Task Replace_RefusesAmbiguityThatDiffersOnlyInWhitespace()
    {
        // The uniqueness check searched for the LITERAL matched text, while matching itself ignores
        // whitespace. So two occurrences that differ only in spacing -- the commonest kind of near
        // duplicate in real source -- read as unique, and the tool silently edits the first.
        var f = Path.Combine(_dir, "dup.cs");
        File.WriteAllText(f, "if (x) {\n\treturn 1;\n}\nif (x)  {\n\treturn 1;\n}\n");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "if (x) {"), ("replacement", "if (y) {"));

        Assert.False(r.Success);
        // The count is part of the signal: "appears 2 times" tells the model how much more context
        // it needs to disambiguate, where "more than once" leaves it guessing.
        Assert.Contains("appears 2 times", r.ErrorMessage ?? "");
        Assert.Contains("Nothing was written", r.ErrorMessage ?? "");
        Assert.Equal("if (x) {\n\treturn 1;\n}\nif (x)  {\n\treturn 1;\n}\n", File.ReadAllText(f));
    }

    // ---- REPLACE: INDENTATION -----------------------------------------------
    // Matching ignores whitespace, but the replacement was inserted VERBATIM. A model that writes
    // standard 4-space C# into a tab-indented file therefore got its match and then silently broke
    // the indentation. Measured live: a real edit turned "\t\t\tif (open)" into "\t\tif (open)",
    // the agent noticed with `cat -A`, reverted its own correct fix, and spent the rest of the run
    // building a heredoc patch through run_shell instead -- abandoning a tool that had worked.

    [Fact]
    public async Task Replace_ReindentsTheReplacementToMatchTheFile()
    {
        var f = Path.Combine(_dir, "tabs.cs");
        File.WriteAllText(f, "\t\t\tif (open) sb.Append(\"x\");\n\t\t\treturn sb;\n");

        // Spaces in, tabs expected out -- the model cannot know the file uses tabs.
        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "if (open) sb.Append(\"x\");"),
                          ("replacement", "        if (open) sb.Append(\"y\");"));

        Assert.True(r.Success, r.ErrorMessage);
        var text = File.ReadAllText(f);
        Assert.Contains("\t\t\tif (open) sb.Append(\"y\");", text);
        Assert.DoesNotContain("        if (open)", text);
    }

    [Fact]
    public async Task Replace_ReindentsEveryLineOfAMultiLineReplacement()
    {
        // The common shape: insert a line above an existing one. Every line of the replacement must
        // land on the file's indentation, not just the first.
        var f = Path.Combine(_dir, "multi.cs");
        File.WriteAllText(f, "\t\tvar a = 1;\n\t\tvar b = 2;\n");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "var b = 2;"),
                          ("replacement", "    // note\n    var b = 3;"));

        Assert.True(r.Success, r.ErrorMessage);
        var lines = File.ReadAllLines(f);
        Assert.Equal("\t\t// note", lines[1]);
        Assert.Equal("\t\tvar b = 3;", lines[2]);
    }

    [Fact]
    public async Task Replace_PreservesRelativeIndentationInsideTheReplacement()
    {
        // Re-indenting must not FLATTEN a block. The replacement's own internal structure (a nested
        // line indented one level deeper) has to survive the shift.
        var f = Path.Combine(_dir, "nest.cs");
        File.WriteAllText(f, "\t\tif (x)\n\t\t{\n\t\t}\n");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "if (x)\n{\n}"),
                          ("replacement", "if (y)\n{\n    Go();\n}"));

        Assert.True(r.Success, r.ErrorMessage);
        var lines = File.ReadAllLines(f);
        Assert.Equal("\t\tif (y)", lines[0]);
        Assert.Equal("\t\t{", lines[1]);
        // The model's OWN nesting, preserved. Shifting moves the block; it does not restyle the
        // interior. The previous engine rebuilt each line and so converted this to a tab — which is
        // also how it managed to reshape blocks it should not have touched.
        Assert.Equal("\t\t    Go();", lines[2]);
        Assert.Equal("\t\t}", lines[3]);
    }

    [Fact]
    public async Task Replace_LeavesAnExactMatchAlone()
    {
        // When the model DID supply the file's real indentation, nothing should be rewritten --
        // re-indentation must be a repair, not a reformat.
        var f = Path.Combine(_dir, "exact.cs");
        File.WriteAllText(f, "\t\tvar a = 1;\n");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "\t\tvar a = 1;"), ("replacement", "\t\tvar a = 2;"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("\t\tvar a = 2;\n", File.ReadAllText(f));
    }

    [Fact]
    public async Task Replace_ReportsWhatItActuallyWrote()
    {
        // "replaced 1 occurrence" gave a doubting model exactly one way to verify: shell out. An
        // agent did precisely that live -- `cat -A`, mistrusted what it saw, reverted its own
        // correct fix, and finished the run patching through run_shell.
        var f = Path.Combine(_dir, "echo.cs");
        File.WriteAllText(f, "\t\tvar a = 1;\n");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "var a = 1;"), ("replacement", "    var a = 2;"));

        var content = (string)r.Output["content"]!;
        Assert.Contains("indentation adjusted", content);
        Assert.Contains("\t\tvar a = 2;", content);   // the model can see the tabs it did not send
    }

    [Fact]
    public async Task Replace_SaysNothingAboutIndentationWhenItChangedNothing()
    {
        // The note must mean something. Appending it to every edit would make it noise the model
        // learns to skip -- including on the one edit where it mattered.
        var f = Path.Combine(_dir, "noop.cs");
        File.WriteAllText(f, "\t\tvar a = 1;\n");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "\t\tvar a = 1;"), ("replacement", "\t\tvar a = 2;"));

        Assert.DoesNotContain("indentation adjusted", (string)r.Output["content"]!);
    }

    [Fact]
    public async Task Replace_TwiceOnTheSameRegionDoesNotCompoundIndentation()
    {
        // NOT IDEMPOTENT was the hole: the first replace shifts the text onto the file's
        // indentation, and a second replace over that ALREADY-SHIFTED text measured its own base
        // from a line that had moved -- adding another level each time. Measured live: a file
        // indented with 3 tabs came out with 5 after two successive edits to the same region.
        var f = Path.Combine(_dir, "twice.cs");
        File.WriteAllText(f, "\t\t\tif (x)\n\t\t\t{\n\t\t\t\tint a = 1;\n\t\t\t}\n");

        var first = await Run(("action", "replace"), ("path", f),
                              ("pattern", "int a = 1;"),
                              ("replacement", "    int a = 2;"));
        Assert.True(first.Success, first.ErrorMessage);

        // Second edit over the region the first one just wrote.
        var second = await Run(("action", "replace"), ("path", f),
                               ("pattern", "int a = 2;"),
                               ("replacement", "    int a = 3;\n    int b = 4;"));
        Assert.True(second.Success, second.ErrorMessage);

        var lines = File.ReadAllLines(f);
        Assert.Equal("\t\t\tif (x)", lines[0]);
        Assert.Equal("\t\t\t{", lines[1]);
        Assert.Equal("\t\t\t\tint a = 3;", lines[2]);   // still FOUR tabs, not five
        Assert.Equal("\t\t\t\tint b = 4;", lines[3]);
        Assert.Equal("\t\t\t}", lines[4]);
    }

    [Fact]
    public async Task Replace_RepeatedIdenticalEditsAreStable()
    {
        // The strongest form: applying an edit whose replacement ALREADY carries the file's exact
        // indentation must leave the file byte-identical however many times it runs.
        var f = Path.Combine(_dir, "stable.cs");
        File.WriteAllText(f, "\t\tvar a = 1;\n");

        for (var i = 0; i < 3; i++)
        {
            var r = await Run(("action", "replace"), ("path", f),
                              ("pattern", "var a = 1;"), ("replacement", "\t\tvar a = 1;"));
            Assert.True(r.Success, r.ErrorMessage);
            Assert.Equal("\t\tvar a = 1;\n", File.ReadAllText(f));
        }
    }

    [Fact]
    public async Task Replace_DoesNotREINDENTANonUniformlyIndentedReplacement()
    {
        // THE LIVE FAILURE. A model disambiguating its match anchors on a comment line: it sends
        // "// comment" at column 0 with the block below carrying its own tabs. The anchor and the
        // body are then in DIFFERENT frames, so no single shift describes the edit -- every rule that
        // assumed one frame produced a wrong answer for the other, and one of them turned three tabs
        // into eight.
        //
        // Aider reaches the same rule from the other direction and encodes it as `if len(add) != 1:
        // return` -- non-uniform indent is not REPAIRED by inventing a base.
        //
        // THE ANCHOR IS NOW PLACED, though, and that is a later correction rather than a regression.
        // Writing it verbatim left a comment flush against column 0 inside a block indented three
        // tabs -- visibly wrong, which was the intent, but measured on two live drives it reads as a
        // deliberate edit and shipped unnoticed. The body lines here quote the file's own tabs, which
        // is the model demonstrating it meant the file's shape; the anchor is the one line whose base
        // it got wrong, and the file supplies it. Nothing is invented: each line takes the depth of
        // the file line its pattern counterpart matched.
        var f = Path.Combine(_dir, "anchor.cs");
        File.WriteAllText(f, "\t\t\t// Handle trailing char\n\t\t\tif (p.HasValue)\n\t\t\t{\n\t\t\t\tint a = 1;\n\t\t\t}\n");

        var r = await Run(("action", "replace"), ("path", f),
                          ("pattern", "// Handle trailing char\n\t\t\tif (p.HasValue)\n\t\t\t{\n\t\t\t\tint a = 1;"),
                          ("replacement",
                           "// Handle trailing char\n\t\t\t\t\tif (p.HasValue)\n\t\t\t\t\t{\n\t\t\t\t\t\tint a = 1;\n\t\t\t\t\t\tNEW();"));

        Assert.True(r.Success, r.ErrorMessage);

        // The critical property is unchanged and still the point: it never GROWS the indentation.
        // The old bug added the file's indent on top of the model's own.
        var lines = File.ReadAllLines(f);

        // The anchor lands at the file's depth rather than column 0 -- the one line the model
        // under-indented, placed from the file rather than guessed.
        Assert.Equal("\t\t\t// Handle trailing char", lines[0]);
        Assert.Equal("\t\t\t\t\tif (p.HasValue)", lines[1]);
        Assert.DoesNotContain(lines, l => l.StartsWith("\t\t\t\t\t\t\t\t", StringComparison.Ordinal));

        // AND IT SAYS SO. The old expectation was "claims nothing, because it adjusted nothing" --
        // true when the anchor was written verbatim. Now that the anchor is placed from the file, the
        // report has to say it: the echoed result is the only channel telling the model its text was
        // moved, and a silent adjustment is the thing this tool is built not to do.
        Assert.Contains("indentation adjusted", (string)r.Output["content"]!);
    }

    [Fact]
    public async Task Search_NamesTheEnclosingFunctionOfEachHit()
    {
        // "file:1196:text" locates a hit only for a reader who already knows the file's shape. A
        // NAME says what the hit is part of. Measured across three drives on a 1,587-line file: the
        // model searched for the flag, got line numbers, and never opened the function one of them
        // was inside -- while correctly describing the bug from the two endpoints it could name.
        var f = Path.Combine(_dir, "scope.cs");
        File.WriteAllText(f, string.Join('\n',
        [
            "public class Widget",
            "{",
            "\tprivate static void WrapCellLine(List<Cell> cells)",
            "\t{",
            "\t\tvar found = TARGET;",
            "\t}",
            "}",
        ]));

        var r = await Run(("action", "search"), ("path", _dir), ("pattern", "TARGET"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("[in WrapCellLine]", (string)r.Output["content"]!);
    }

    [Fact]
    public async Task Search_FallsBackToTheTypeWhenNoFunctionEncloses()
    {
        var f = Path.Combine(_dir, "field.cs");
        File.WriteAllText(f, string.Join('\n',
        [
            "public class Holder",
            "{",
            "\tprivate int TARGET = 1;",
            "}",
        ]));

        var r = await Run(("action", "search"), ("path", _dir), ("pattern", "TARGET"));

        Assert.Contains("[in Holder]", (string)r.Output["content"]!);
    }

    [Fact]
    public async Task Search_SaysNothingWhenNothingEncloses()
    {
        // A top-level hit must not gain an invented label -- a wrong name is worse than no name.
        var f = Path.Combine(_dir, "top.txt");
        File.WriteAllText(f, "TARGET at top level\n");

        var r = await Run(("action", "search"), ("path", _dir), ("pattern", "TARGET"));

        Assert.DoesNotContain("[in ", (string)r.Output["content"]!);
    }

    // ---- relative paths resolve against the AGENT's folder, not the process's -------------------

    /// <summary>
    /// THE SYSTEM PROMPT PROMISES THIS: "Relative paths resolve from the working directory." Until
    /// the context carried a root it was true only by coincidence — the process happened to be
    /// pointed at the same place — and a second session rooted elsewhere would have broken it
    /// silently, writing into a checkout the user never approved.
    /// </summary>
    [Fact]
    public async Task RelativePath_ResolvesAgainstTheContextsWorkingDirectory()
    {
        var ctx = new TestJobContext { WorkingDirectory = _dir };
        Directory.CreateDirectory(Path.Combine(_dir, "nested"));   // the plugin writes, it does not mkdir

        var write = await new FileJobPlugin().ExecuteAsync(
            P(("action", "write"), ("path", "nested/f.txt"), ("content", "landed")),
            ctx, CancellationToken.None);

        // The file exists under the GIVEN root...
        Assert.True(write.Success, write.ErrorMessage);
        Assert.Equal("landed", File.ReadAllText(Path.Combine(_dir, "nested", "f.txt")));

        // ...and NOT under the process's, which is where it went before.
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "nested", "f.txt")));
    }

    /// <summary>
    /// A DROPPED LEADING SLASH IS NAMED, not left as "not found".
    ///
    /// <para>A model that means /tmp/x/App.cs and sends "tmp/x/App.cs" has written a relative path
    /// that looks absolute. Resolving it against /tmp/x is correct and yields /tmp/x/tmp/x/App.cs,
    /// and the framework's "Could not find a part of the path" reads to a model as "the file is
    /// missing" — so it hunts for the file instead of fixing the path.</para>
    ///
    /// <para>MEASURED: 450 of these across three drives. One planner spent all eleven of its turns
    /// on them and returned having written nothing, which looked like an agent ignoring its
    /// briefing.</para>
    /// </summary>
    [Fact]
    public async Task APathMissingItsLeadingSeparator_SaysSo()
    {
        var ctx = new TestJobContext { WorkingDirectory = _dir };

        // _dir is absolute, e.g. /tmp/xyz — send it back with the leading separator dropped.
        var mangled = _dir.TrimStart(Path.DirectorySeparatorChar) + "/f.txt";

        var result = await new FileJobPlugin().ExecuteAsync(
            P(("action", "read"), ("path", mangled)), ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("leading separator", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains(mangled, result.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>An ordinary missing file is still an ordinary missing file — the hint must not fire
    /// on every not-found, or it becomes noise that hides the real cause.</summary>
    [Fact]
    public async Task AnOrdinaryMissingFile_DoesNotClaimASeparatorProblem()
    {
        var ctx = new TestJobContext { WorkingDirectory = _dir };

        var result = await new FileJobPlugin().ExecuteAsync(
            P(("action", "read"), ("path", "nope/missing.txt")), ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("leading separator", result.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>Reading takes the same base as writing — otherwise a tool could write a file it
    /// then cannot find.</summary>
    [Fact]
    public async Task RelativePath_ReadsFromTheSameRootItWroteTo()
    {
        var ctx = new TestJobContext { WorkingDirectory = _dir };
        File.WriteAllText(Path.Combine(_dir, "here.txt"), "found me");

        var read = await new FileJobPlugin().ExecuteAsync(
            P(("action", "read"), ("path", "here.txt")), ctx, CancellationToken.None);

        Assert.True(read.Success, read.ErrorMessage);
        Assert.Equal("found me", (string)read.Output["content"]!);
    }

    /// <summary>`dest` is a path too. A copy that resolved its source against the agent's folder and
    /// its destination against the process's would be the same split-brain write, one layer down.</summary>
    [Fact]
    public async Task RelativePath_AppliesToCopyDestination()
    {
        var ctx = new TestJobContext { WorkingDirectory = _dir };
        File.WriteAllText(Path.Combine(_dir, "src.txt"), "copy me");
        Directory.CreateDirectory(Path.Combine(_dir, "out"));

        var copy = await new FileJobPlugin().ExecuteAsync(
            P(("action", "copy"), ("path", "src.txt"), ("dest", "out/dst.txt")),
            ctx, CancellationToken.None);

        Assert.True(copy.Success, copy.ErrorMessage);
        Assert.Equal("copy me", File.ReadAllText(Path.Combine(_dir, "out", "dst.txt")));
    }

    /// <summary>WITH NO ROOT GIVEN, nothing changes: the process's own directory, exactly as every
    /// caller behaved before the property existed. An absolute path is unaffected either way.</summary>
    [Fact]
    public async Task WithNoWorkingDirectory_AnAbsolutePathIsUntouched()
    {
        var path = Path.Combine(_dir, "abs.txt");

        var write = await new FileJobPlugin().ExecuteAsync(
            P(("action", "write"), ("path", path), ("content", "absolute")),
            new TestJobContext(), CancellationToken.None);

        Assert.True(write.Success, write.ErrorMessage);
        Assert.Equal("absolute", File.ReadAllText(path));
    }
}
