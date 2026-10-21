using CxAgent.Plugins.RepoMap.Parsing;
using Xunit;
using TS = TreeSitter;

namespace CxAgent.Plugins.RepoMap.Tests;

public class GrammarTests
{
    /// <summary>
    /// EVERY QUERY COMPILES AGAINST ITS GRAMMAR. A query that does not is a language the map silently
    /// reads as text, and the only other place that would show is one line of one map's header.
    /// </summary>
    [Fact]
    public void EveryQueryCompiles()
    {
        Assert.Equal(14, Grammars.All.Count);
        foreach (var grammar in Grammars.All)
        {
            using var language = new TS.Language(grammar.Id);
            var ex = Record.Exception(() => { using var query = new TS.Query(language, Grammars.QueryText(grammar)); });
            Assert.True(ex is null, $"{grammar.Name}: {ex?.Message}");
        }
    }

    [Theory]
    [InlineData("a.c", "c"), InlineData("a.h", "cpp"), InlineData("a.hpp", "cpp"), InlineData("a.cc", "cpp")]
    [InlineData("a.cs", "c-sharp"), InlineData("A.java", "java"), InlineData("a.scala", "scala")]
    [InlineData("a.js", "javascript"), InlineData("a.jsx", "javascript"), InlineData("a.mjs", "javascript")]
    [InlineData("a.ts", "typescript"), InlineData("a.mts", "typescript"), InlineData("a.tsx", "tsx")]
    [InlineData("a.go", "go"), InlineData("a.rs", "rust"), InlineData("a.py", "python"), InlineData("a.pyi", "python")]
    [InlineData("a.rb", "ruby"), InlineData("a.php", "php"), InlineData("a.sh", "bash"), InlineData("a.zsh", "bash")]
    public void AnExtensionWithAGrammar_GetsIt(string path, string id) => Assert.Equal(id, Grammars.For(path)?.Id);

    [Theory]
    [InlineData("a.kt"), InlineData("a.kts"), InlineData("a.swift"), InlineData("a.dart"), InlineData("a.m"), InlineData("a.mm")]
    public void TheFallbackLanguages_HaveNoGrammar(string path) => Assert.Null(Grammars.For(path));

    /// <summary>Each query file names where it came from, so a reader can check it against upstream.</summary>
    [Fact]
    public void EveryQueryNamesItsSource()
    {
        foreach (var grammar in Grammars.All)
            Assert.Contains("; Source:", Grammars.QueryText(grammar));
    }
}
