using CxAgent.Plugins.CloneFinder;
using Xunit;

namespace CxAgent.Plugins.CloneFinder.Tests;

public class ScannerTests
{
    /// <summary>Builds a throwaway tree, scans it, and reports what survived as root-relative
    /// paths with forward slashes — assertions stay readable and the temp directory's random
    /// name never leaks into an expected value.</summary>
    private static IReadOnlyList<string> Scan(Action<string> build, string? exclude = null)
    {
        var root = Directory.CreateTempSubdirectory("clone-scanner-").FullName;
        try
        {
            build(root);
            return Scanner.Files(new ScanRequest(root, Exclude: exclude))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .ToList();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string root, string relative, string content = "class C { }")
    {
        var full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>bin/ and friends are excluded even in a repo with no .gitignore at all — build
    /// output is full of generated duplicates that would drown every real finding.</summary>
    [Fact]
    public void BuiltInDirectoriesAreNeverScanned()
    {
        var files = Scan(root =>
        {
            Write(root, "src/Kept.cs");
            Write(root, "bin/Debug/Compiled.cs");
            Write(root, "obj/Generated.cs");
            Write(root, "node_modules/lib/index.js");
            Write(root, "vendor/dep.cs");
            Write(root, "README.md"); // not code: never offered to the tokeniser
        });

        Assert.Equal(["src/Kept.cs"], files);
    }

    /// <summary>The built-in list cannot know what THIS repository ignores; its .gitignore can,
    /// and both a directory entry and a suffix glob must hold.</summary>
    [Fact]
    public void GitignoreEntriesAreHonoured()
    {
        var files = Scan(root =>
        {
            Write(root, ".gitignore", "generated/\n*.g.cs\n");
            Write(root, "Kept.cs");
            Write(root, "Model.g.cs");
            Write(root, "generated/Api.cs");
        });

        Assert.Equal(["Kept.cs"], files);
    }

    /// <summary>The caller's excludes compose with the other layers rather than replacing them —
    /// what a run wants to skip (migrations, a vendored tree the repo commits) is not in any
    /// .gitignore precisely because it is committed.</summary>
    [Fact]
    public void CallerExcludeGlobsAreApplied()
    {
        var files = Scan(root =>
        {
            Write(root, "Kept.cs");
            Write(root, "Migrations/20240101_Init.cs");
        }, exclude: "Migrations/**");

        Assert.Equal(["Kept.cs"], files);
    }

    /// <summary>TESTS ARE SCANNED. Duplicated test setup is real duplication, and skipping tests
    /// by default would be a strong opinion applied silently.</summary>
    [Fact]
    public void FilesUnderTestsAreScanned()
    {
        var files = Scan(root =>
        {
            Write(root, "src/Thing.cs");
            Write(root, "tests/ThingTests.cs");
        });

        Assert.Equal(["src/Thing.cs", "tests/ThingTests.cs"], files);
    }
}
