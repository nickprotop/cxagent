using CxAgent.Plugins.RepoMap;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

public class RankerTests
{
    private static FileOutline File(string path, string[] declares, params string[] mentions) =>
        new(path,
            declares.Select((d, i) => new Symbol(d, $"class {d}", i + 1, 0, SymbolKind.Type)).ToList(),
            mentions.GroupBy(m => m).ToDictionary(g => g.Key, g => g.Count()));

    /// <summary>The file everything else is built on ranks above the files built on it.</summary>
    [Fact]
    public void TheFileOthersUse_RanksFirst()
    {
        var files = new[]
        {
            File("a/Leaf1.cs", ["LeafOne"], "SessionCore"),
            File("a/Leaf2.cs", ["LeafTwo"], "SessionCore"),
            File("a/Leaf3.cs", ["LeafThree"], "SessionCore", "LeafOne"),
            File("core/SessionCore.cs", ["SessionCore"]),
        };

        var ranking = Ranker.Rank(files, []);

        Assert.Equal("core/SessionCore.cs", ranking.Files[0].Path);
    }

    /// <summary>
    /// A PLAIN WORD LINKS LESS THAN A COMPOUND NAME. Every file mentions <c>Message</c> for some
    /// reason; one that mentions <c>SessionFactory</c> means the type.
    /// </summary>
    [Fact]
    public void ACompoundName_OutweighsAPlainWord()
    {
        Assert.True(Ranker.Distinctiveness("SessionFactory") > Ranker.Distinctiveness("Message"));
        Assert.True(Ranker.Distinctiveness("cxp_call_result") > Ranker.Distinctiveness("count"));
        Assert.True(Ranker.Distinctiveness("_privateHelperName") < Ranker.Distinctiveness("count"));
    }

    /// <summary>
    /// A METHOD'S NAME DOES NOT LINK FILES. <c>Close</c> declared inside a type turns up in every file
    /// that closes anything; only the type's own name says the type is used.
    /// </summary>
    [Fact]
    public void AMethodName_DoesNotPullAFileUp()
    {
        var dialog = new FileOutline("ui/Dialog.cs",
            [new Symbol("DialogBox", "class DialogBox", 1, 0, SymbolKind.Type), new Symbol("CloseWindow", "void CloseWindow()", 2, 1, SymbolKind.Member)],
            new Dictionary<string, int>());
        var engine = File("core/EngineCore.cs", ["EngineCore"]);
        // THE METHOD IS MENTIONED MORE than the type, so a ranker that let method names link files
        // would put the dialog first — and this test would see it.
        var users = Enumerable.Range(0, 5)
            .Select(i => File($"u/U{i}.cs", [$"UserNumber{i}"], "CloseWindow", "CloseWindow", "CloseWindow", "CloseWindow", "EngineCore"))
            .ToList();

        var ranking = Ranker.Rank([dialog, engine, .. users], []);

        Assert.True(ranking.Files.ToList().FindIndex(f => f.Path == "core/EngineCore.cs")
                    < ranking.Files.ToList().FindIndex(f => f.Path == "ui/Dialog.cs"));
    }

    /// <summary>With a focus, the focused file and what it uses come before what the rest of the code uses most.</summary>
    [Fact]
    public void Focus_PutsTheFocusedFileAndWhatItUsesFirst()
    {
        var files = new[]
        {
            File("core/SessionCore.cs", ["SessionCore"]),
            File("a/One.cs", ["FeatureOne"], "SessionCore"),
            File("a/Two.cs", ["FeatureTwo"], "SessionCore"),
            File("b/Plugin.cs", ["PluginThing"], "HelperUtil"),
            File("b/Helper.cs", ["HelperUtil"]),
        };

        var ranking = Ranker.Rank(files, ["b/Plugin.cs"]);

        Assert.Equal(["b/Plugin.cs", "b/Helper.cs"], ranking.Files.Take(2).Select(f => f.Path));
    }

    [Fact]
    public void Focus_AcceptsAFolderASuffixAndASymbol()
    {
        var files = new[]
        {
            File("src/core/Alpha.cs", ["AlphaType"]),
            File("src/other/Beta.cs", ["BetaType"]),
            File("src/other/Gamma.cs", ["GammaType"]),
        };

        Assert.Equal("src/core/Alpha.cs", Ranker.Rank(files, ["core"]).Files[0].Path);
        Assert.Equal("src/other/Gamma.cs", Ranker.Rank(files, ["Gamma.cs"]).Files[0].Path);
        Assert.Equal("src/other/Beta.cs", Ranker.Rank(files, ["BetaType"]).Files[0].Path);
    }

    [Fact]
    public void AFocusThatMatchesNothing_IsReported()
    {
        var ranking = Ranker.Rank([File("x/A.cs", ["AlphaType"])], ["NoSuchThing", "x/A.cs"]);
        Assert.Equal(["NoSuchThing"], ranking.Unmatched);
    }
}
