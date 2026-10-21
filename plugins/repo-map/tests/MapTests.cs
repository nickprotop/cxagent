using System.Text.RegularExpressions;
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

    /// <summary>
    /// GIT ALSO SAYS WHAT IS VENDORED OR GENERATED, through .gitattributes — GitHub's own
    /// <c>linguist-vendored</c> and <c>linguist-generated</c>. A file marked either way is listed,
    /// and ranked aside.
    /// </summary>
    [Fact]
    public void GitattributesMarkingVendoredOrGenerated_IsRead()
    {
        using var tree = new TempTree()
            .Write(".gitattributes", "lib/cJSON.c linguist-vendored\napi.pb.go linguist-generated=true\nown.c linguist-vendored=false\n")
            .Write("lib/cJSON.c", "int cJSON_Parse(void) { return 0; }\n")
            .Write("api.pb.go", "package a\n")
            .Write("own.c", "int own(void) { return 0; }\n");
        tree.Git("init", "-q").Git("add", ".");

        var list = FileSource.List(tree.Root, _ => { });
        var marked = list.Vendored.Select(f => Path.GetRelativePath(tree.Root, f).Replace('\\', '/')).Order().ToList();

        Assert.Equal(["api.pb.go", "lib/cJSON.c"], marked);
    }

    /// <summary>
    /// A HOST THAT CANNOT RECORD git DOES NOT COST THE LIST. A host older than 0.9.44 throws when
    /// the process exits in the instant it is being recorded; git's answer is still the one to use.
    /// </summary>
    [Fact]
    public void AHostThatThrowsOnRegistering_StillGetsGitsList()
    {
        using var tree = new TempTree().Write(".gitignore", "skip/\n").Write("a.cs", "class A {}").Write("skip/b.cs", "class B {}");
        tree.Git("init", "-q");

        var list = FileSource.List(tree.Root, _ => throw new System.ComponentModel.Win32Exception());

        Assert.True(list.FromGit);
        Assert.Equal(["a.cs"], list.Files.Select(f => Path.GetRelativePath(tree.Root, f)));
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

    [Fact]
    public async Task MissingNatives_ReadEverythingAsTextAndSaySo()
    {
        using var tree = new TempTree().Write("a.py", "class Model:\n    pass\n").Write("b.py", "x = Model()\n");
        using var engine = Parsing.Engine.Unavailable("libtree-sitter.so: cannot open shared object file");

        var map = await RepoMap.Map.Build(new MapRequest(tree.Root, ".", []) { Engine = engine }, _ => { }, default);

        Assert.Contains("class Model", map);
        Assert.Single(Regex.Matches(map, "Parsed without Tree-sitter: libtree-sitter.so"));
    }

    [Fact]
    public async Task AQueryThatDoesNotCompile_IsNamedInTheHeader_AndItsFilesAreStillMapped()
    {
        using var tree = new TempTree().Write("a.py", "class Model:\n    pass\n");
        using var engine = Parsing.Engine.WithQueries(g => g == Parsing.Grammars.Python ? "(no_such_node) @name" : Parsing.Grammars.QueryText(g));

        var map = await RepoMap.Map.Build(new MapRequest(tree.Root, ".", []) { Engine = engine }, _ => { }, default);

        Assert.Contains("Python read as text: ", map);
        Assert.Contains("class Model", map);
    }

    /// <summary>A KOTLIN FILE BESIDE PARSED ONES is read as text, and that is the normal path — no note.</summary>
    [Fact]
    public async Task AFallbackLanguage_IsReadAsTextWithoutANote()
    {
        using var tree = new TempTree().Write("A.kt", "class Widget {\n    fun draw() {}\n}\n").Write("b.py", "def f():\n    pass\n");
        var map = await RepoMap.Map.Build(new MapRequest(tree.Root, ".", []), _ => { }, default);

        Assert.Contains("class Widget", map);
        Assert.Contains("def f()", map);
        Assert.DoesNotContain("read as text", map);
        Assert.DoesNotContain("without Tree-sitter", map);
    }

    /// <summary>A FILE .gitattributes CALLS VENDORED RANKS BELOW THE PROJECT'S OWN, however much the project calls it.</summary>
    [Fact]
    public async Task AVendoredFile_RanksBelowTheProjectsOwn()
    {
        using var tree = new TempTree()
            .Write(".gitattributes", "lib/cjson.c linguist-vendored\n")
            .Write("lib/cjson.c", "int cjson_parse(void) { return 0; }\n")
            .Write("engine.c", "int engine_core(void) { return 0; }\n")
            .Write("a.c", "int alpha_user(void) { return engine_core() + cjson_parse(); }\n")
            .Write("b.c", "int beta_user(void) { return engine_core() + cjson_parse(); }\n")
            .Write("c.c", "int gamma_user(void) { return cjson_parse(); }\n")
            .Write("d.c", "int delta_user(void) { return cjson_parse(); }\n");
        tree.Git("init", "-q").Git("add", ".");

        var map = await Map(tree);

        Assert.True(map.IndexOf("engine.c\n", StringComparison.Ordinal) < map.IndexOf("lib/cjson.c\n", StringComparison.Ordinal), map);
    }
}
