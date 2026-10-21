using System.Diagnostics;
using CxAgent.Plugins.RepoMap;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

/// <summary>A throwaway folder, removed when the test ends.</summary>
internal sealed class TempTree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("repo-map-").FullName;

    public TempTree Write(string relative, string content)
    {
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    /// <summary>Runs git here and fails the test if it fails — a fixture that did not build is not a result.</summary>
    public TempTree Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var git = Process.Start(psi)!;
        var stderr = git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr}");
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class FileSourceTests
{
    private static List<string> Listed(TempTree tree) =>
        FileSource.List(tree.Root, _ => { }).Files
            .Select(f => Path.GetRelativePath(tree.Root, f).Replace('\\', '/'))
            .ToList();

    /// <summary>
    /// GIT DECIDES: what the repository ignores is left out, and an untracked file that is not
    /// ignored is in — someone writing it now is asking about it.
    /// </summary>
    [Fact]
    public void InARepository_GitsIgnoreRulesDecide()
    {
        using var tree = new TempTree()
            .Write(".gitignore", "generated/\n")
            .Write("src/Tracked.cs", "class A {}")
            .Write("generated/Gen.cs", "class G {}")
            .Write("bin/Committed.cs", "class B {}")
            .Write("notes.md", "# not code");
        tree.Git("init", "-q").Git("add", "src", ".gitignore").Git("add", "-f", "bin");
        tree.Write("src/New.cs", "class N {}");

        var files = Listed(tree);

        // bin/ IS IN: the repository committed it, and git's word stands over a guess.
        Assert.Equal(["bin/Committed.cs", "src/New.cs", "src/Tracked.cs"], files);
    }

    [Fact]
    public void WithoutARepository_TheWalkSkipsBuildOutputAndDotFolders()
    {
        using var tree = new TempTree()
            .Write("src/A.cs", "class A {}")
            .Write("bin/B.cs", "class B {}")
            .Write("node_modules/x/c.js", "function c() {}")
            .Write(".hidden/D.cs", "class D {}");

        Assert.Equal(["src/A.cs"], Listed(tree));
    }

    /// <summary>A LINKED FOLDER IS NOT FOLLOWED, or a map of this folder would read somewhere else.</summary>
    [Fact]
    public void ASymlinkedFolder_IsNotFollowed()
    {
        using var outside = new TempTree().Write("Secret.cs", "class Secret {}");
        using var tree = new TempTree().Write("src/A.cs", "class A {}");
        Directory.CreateSymbolicLink(Path.Combine(tree.Root, "linked"), outside.Root);

        Assert.Equal(["src/A.cs"], Listed(tree));
    }

    /// <summary>
    /// AN EMPTY FILE IS SKIPPED, and with it a FIFO, which .NET cannot tell from one: opening a FIFO
    /// to read it blocks until something writes, and the map would hang.
    /// </summary>
    [Fact]
    public void AFifo_IsSkippedRatherThanOpened()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tree = new TempTree().Write("ok.py", "def ok(): pass");
        using (var mkfifo = Process.Start("mkfifo", Path.Combine(tree.Root, "pipe.py"))!) mkfifo.WaitForExit();

        Assert.Equal(["ok.py"], Listed(tree));
    }

    [Fact]
    public void MinifiedAndHugeFiles_AreSkipped()
    {
        using var tree = new TempTree()
            .Write("app.min.js", "function a(){}")
            .Write("big.cs", new string('x', (int)FileSource.MaxBytes + 1))
            .Write("ok.cs", "class Ok {}");

        Assert.Equal(["ok.cs"], Listed(tree));
    }
}

public class MapTests
{
    private static Task<string> Map(TempTree tree, int tokens = RepoMap.Map.DefaultTokens, params string[] focus) =>
        RepoMap.Map.Build(new MapRequest(tree.Root, ".", focus, tokens), _ => { }, CancellationToken.None);

    [Fact]
    public async Task AMap_ShowsDeclarationsWithLineNumbers()
    {
        using var tree = new TempTree()
            .Write("Core.cs", "public class CoreEngine\n{\n    public void Start(int port) { }\n}\n")
            .Write("User.cs", "class UserOfCore { CoreEngine e; }\n");

        var map = await Map(tree);

        Assert.Contains("Core.cs\n  1: public class CoreEngine\n    3: public void Start(int port)\n", map);
        Assert.StartsWith("2 source files under ., 2 declaring something.", map);
    }

    /// <summary>
    /// THE OUTLINE IS FENCED. Tool output is rendered as markdown, which strips the indentation that
    /// shows a member inside its type and swallows <c>&lt;T&gt;</c> as a tag; a fence shows it as written.
    /// </summary>
    [Fact]
    public async Task TheOutline_IsInACodeFence()
    {
        using var tree = new TempTree()
            .Write("Core.cs", "public class CoreEngine\n{\n    public List<int> Start(int port) { }\n}\n");

        var map = await Map(tree);

        Assert.Contains("```\nCore.cs\n  1: public class CoreEngine\n    3: public List<int> Start(int port)\n```", map);
    }

    /// <summary>
    /// WHAT DOES NOT FIT IS STILL NAMED, and the map says what it left out — a reader must never
    /// mistake a cut map for the whole repository.
    /// </summary>
    [Fact]
    public async Task OverBudget_TheRestIsListedByPathAndCounted()
    {
        using var tree = new TempTree();
        for (var i = 0; i < 60; i++)
            tree.Write($"src/File{i:00}.cs",
                string.Join("\n", Enumerable.Range(0, 10).Select(j => $"public class Type{i}x{j} {{ public void Method{j}(int argument) {{ }} }}")));

        var map = await Map(tree, tokens: RepoMap.Map.MinTokens);

        Assert.Contains("Next by rank, declarations not shown:", map);
        Assert.Contains("more files.", map);
        Assert.InRange(map.Length, 1, RepoMap.Map.MinTokens * 4 + 600);
    }

    /// <summary>THE FIRST FILE IS SHOWN HOWEVER SMALL THE BUDGET: a header with no files answers nothing.</summary>
    [Fact]
    public async Task TheFirstFile_IsShownEvenWhenItAloneIsOverBudget()
    {
        using var tree = new TempTree()
            .Write("Big.cs", "public class Big\n{\n" + string.Join("\n", Enumerable.Range(0, 25).Select(j => $"    public void AVeryLongMethodNameNumber{j}(int a, int b, int c, string d) {{ }}")) + "\n}\n");

        var map = await Map(tree, tokens: RepoMap.Map.MinTokens);

        Assert.Contains("Big.cs\n", map);
    }

    /// <summary>A FILE THAT DECLARES TOO MUCH SHOWS ITS TYPES AND ITS MOST-USED MEMBERS, and counts the rest.</summary>
    [Fact]
    public async Task AFileWithManyDeclarations_KeepsItsTypesAndCountsTheRest()
    {
        using var tree = new TempTree()
            .Write("Many.cs", "public class Many\n{\n" + string.Join("\n", Enumerable.Range(0, 40).Select(j => $"    private void Helper{j}() {{ }}")) + "\n    public void UsedEverywhere() { }\n}\n")
            .Write("User.cs", "class User { void M() { new Many().UsedEverywhere(); } }\n");

        var map = await Map(tree, tokens: RepoMap.Map.MaxTokens);

        Assert.Contains("public class Many", map);
        Assert.Contains("public void UsedEverywhere()", map);
        Assert.Contains("less-used declarations not shown", map);
    }

    /// <summary>A FOCUS THAT MATCHED NOTHING DOES NOT CLAIM TO CENTRE THE MAP: the whole folder was mapped, and the header says so.</summary>
    [Fact]
    public async Task AFocusThatMatchedNothing_IsNotClaimedAsTheCentre()
    {
        using var tree = new TempTree().Write("A.cs", "class Alpha {}");

        var map = await Map(tree, RepoMap.Map.DefaultTokens, "Nope");

        Assert.DoesNotContain("Centred on", map);
        Assert.Contains("No file, folder or symbol matched: Nope.", map);
    }

    [Fact]
    public async Task AFolderWithNoCode_SaysSo()
    {
        using var tree = new TempTree().Write("README.md", "# hi");
        Assert.Equal("No source files under . in a language repo_map reads.", await Map(tree));
    }
}
