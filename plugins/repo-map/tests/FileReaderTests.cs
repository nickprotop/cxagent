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
}
