using CxAgent.Plugins.RepoMap.Parsing;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

/// <summary>Which reader a file's outline comes from.</summary>
public sealed class FileReaderTests : IDisposable
{
    private readonly Engine _engine = Engine.Open();

    public void Dispose() => _engine.Dispose();

    private static string Signatures(FileOutline outline) => string.Join(" | ", outline.Symbols.Select(s => s.Signature));

    /// <summary>
    /// A FILE THE GRAMMAR CANNOT READ IS NOT A FILE WITH NO DECLARATIONS. A macro the parser cannot
    /// expand — fmt opens every header with <c>FMT_BEGIN_NAMESPACE</c> — breaks the declaration after
    /// it, and the text reader, which skips what it does not recognise, still sees the class.
    /// </summary>
    [Fact]
    public void AFileTheGrammarCannotRead_IsReadByWhicheverReaderFindsMore()
    {
        const string source = "FMT_BEGIN_NAMESPACE\nFMT_BEGIN_EXPORT\n\ntemplate <typename Char> class context {\n public:\n  void advance();\n};\n";
        var outline = FileReader.Outline("a.h", source, Language.For("a.h")!, _engine);
        Assert.Contains("template <typename Char> class context", Signatures(outline));
        Assert.Contains("void advance()", Signatures(outline));
    }

    /// <summary>A FILE THAT PARSES CLEANLY IS THE TREE'S, even where the text reader would list more — its extra lines are the misreadings the tree exists to avoid.</summary>
    [Fact]
    public void AFileThatParsesCleanly_IsTheTrees()
    {
        // The text reader lists `private readonly object _lock` as a type; the tree, rightly, does not.
        const string source = "class A\n{\n    private readonly object _lock;\n    void Run() { }\n}\n";
        var outline = FileReader.Outline("A.cs", source, Language.For("A.cs")!, _engine);
        Assert.Equal("class A | void Run()", Signatures(outline));
    }

    [Fact]
    public void ALanguageWithoutAGrammar_IsReadAsText()
    {
        var outline = FileReader.Outline("A.kt", "class Widget {\n    fun draw() {}\n}\n", Language.For("A.kt")!, _engine);
        Assert.Contains("class Widget", Signatures(outline));
    }

    /// <summary>
    /// THE RANKING COUNTS WHAT THE CODE MENTIONS, AS TEXT. Measured on 13 repositories, edges from
    /// the parser's references alone ranked flask's app, zod's types and cxagent's Agent far lower than
    /// every word the code uses does — a name passed as a value or reached through an alias is a use
    /// too, and no query marks it. So a parsed file keeps its declarations from the tree and its
    /// mentions from the text.
    /// </summary>
    [Fact]
    public void AParsedFilesMentions_AreEveryNameItsCodeUses()
    {
        var outline = FileReader.Outline("a.py", "handler = Model\nregistry[key] = Session\n# Comment only\nlabel = \"Quoted\"\n", Language.For("a.py")!, _engine);
        Assert.Contains("Model", outline.Mentions.Keys);
        Assert.Contains("Session", outline.Mentions.Keys);
        Assert.DoesNotContain("Comment", outline.Mentions.Keys);
        Assert.DoesNotContain("Quoted", outline.Mentions.Keys);
    }

    /// <summary>
    /// AN ANNOTATION IS A LABEL, NOT A DEPENDENCY. Every guava file imports and applies
    /// <c>@GwtCompatible</c>; ranked by those uses, the annotation would outrank the classes the
    /// files are actually built from. It is listed in its own file, and links nothing.
    /// </summary>
    [Fact]
    public void AnAnnotationUsedEverywhere_DoesNotOutrankWhatTheFilesUse()
    {
        FileOutline Read(string path, string source) => FileReader.Outline(path, source, Language.For(path)!, _engine);
        var files = new List<FileOutline>
        {
            Read("Marked.java", "package p;\npublic @interface Marked {}\n"),
            Read("Core.java", "package p;\npublic class Core { public void run() {} }\n"),
        };
        // As in guava: the annotation on every file, the class it is built on in some of them.
        for (var i = 0; i < 6; i++)
            files.Add(Read($"User{i}.java", i < 2
                ? $"package p;\nimport p.Marked;\n@Marked\nclass User{i} {{ void f(Core c) {{ }} }}\n"
                : $"package p;\nimport p.Marked;\n@Marked\nclass User{i} {{ }}\n"));

        var ranked = Ranker.Rank(files, []).Files.Select(f => f.Path).ToList();
        Assert.True(ranked.IndexOf("Core.java") < ranked.IndexOf("Marked.java"), string.Join(", ", ranked));
        Assert.Contains("public @interface Marked", files[0].Symbols.Select(s => s.Signature));
    }
}
