using CxAgent.Plugins.RepoMap.Parsing;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

public class EngineTests
{
    [Fact]
    public void AGrammar_GetsOneReaderForTheWholeMap()
    {
        using var engine = Engine.Open();
        var first = engine.ReaderFor(Grammars.Python);
        Assert.NotNull(first);
        Assert.Same(first, engine.ReaderFor(Grammars.Python));
        Assert.Empty(engine.Notes);
    }

    [Fact]
    public void WithoutNatives_NoGrammarHasAReader_AndTheNoteSaysWhyOnce()
    {
        using var engine = Engine.Unavailable("libtree-sitter.so: cannot open shared object file");
        Assert.Null(engine.ReaderFor(Grammars.Python));
        Assert.Null(engine.ReaderFor(Grammars.Go));
        Assert.Equal(["Parsed without Tree-sitter: libtree-sitter.so: cannot open shared object file"], engine.Notes);
    }

    [Fact]
    public void AQueryThatDoesNotCompile_SendsOnlyItsLanguageToTheTextReader()
    {
        using var engine = Engine.WithQueries(g => g == Grammars.Python ? "(no_such_node) @name" : Grammars.QueryText(g));
        Assert.Null(engine.ReaderFor(Grammars.Python));
        Assert.Null(engine.ReaderFor(Grammars.Python));   // asked again: still one note
        Assert.NotNull(engine.ReaderFor(Grammars.Go));
        var note = Assert.Single(engine.Notes);
        Assert.StartsWith("Python read as text: ", note);
    }

    /// <summary>A PARSE THAT RUNS OUT OF TIME gives up, and the parser reads the next file as if nothing happened.</summary>
    [Fact]
    public void AParseOutOfTime_GivesNothing_AndTheParserStillWorks()
    {
        using var engine = Engine.Open();
        var reader = engine.ReaderFor(Grammars.Python)!;
        var source = string.Concat(Enumerable.Repeat("def f(a, b):\n    return a + b\n", 20_000));

        Assert.Null(TreeOutliner.Outline("big.py", source, reader, TimeSpan.Zero));
        Assert.NotNull(TreeOutliner.Outline("small.py", "def g():\n    pass\n", reader, TreeOutliner.ParseBudget));
    }
}
