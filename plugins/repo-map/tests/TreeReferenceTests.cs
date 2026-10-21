using CxAgent.Plugins.RepoMap.Parsing;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

public sealed class TreeReferenceTests : IDisposable
{
    private readonly Engine _engine = Engine.Open();

    private FileOutline Read(string path, string source) =>
        TreeOutliner.Outline(path, source, _engine.ReaderFor(Grammars.For(path)!)!)!;

    public void Dispose() => _engine.Dispose();

    /// <summary>
    /// PYTHON'S UPSTREAM QUERY MARKS ONLY CALLS. A file that uses another's class only as a type hint
    /// and a base class still uses it, and the ranking has to see that.
    /// </summary>
    [Fact]
    public void TypeHintsAndBaseClasses_LinkToTheFileThatDeclaresThem()
    {
        var model = Read("model.py", "class Model:\n    pass\n\nclass Session:\n    pass\n");
        var user = Read("views.py", "from model import Session\n\nclass View(Model):\n    def handle(self, session: Session) -> None:\n        pass\n");

        Assert.Contains("Model", user.Mentions.Keys);
        Assert.Contains("Session", user.Mentions.Keys);
        Assert.Equal("model.py", Ranker.Rank([model, user], []).Files[0].Path);
    }

    [Fact]
    public void ANameOnlyInACommentOrString_DoesNotLink()
    {
        var user = Read("views.py", "# Model is mentioned here\ndef handle():\n    return \"Session\"\n");
        Assert.DoesNotContain("Model", user.Mentions.Keys);
        Assert.DoesNotContain("Session", user.Mentions.Keys);
    }

    /// <summary>A DECLARATION DOES NOT MENTION ITSELF — or every file would use its own types, and Ruby's constants would count twice.</summary>
    [Fact]
    public void ADefinitionsOwnName_IsNotAMention()
    {
        var file = Read("a.rb", "class Widget\nend\n");
        Assert.DoesNotContain("Widget", file.Mentions.Keys);
    }

    [Fact]
    public void Mentions_AreCounted()
    {
        var file = Read("a.go", "package a\n\nfunc f() { helper(); helper(); other() }\n");
        Assert.Equal(2, file.Mentions["helper"]);
        Assert.Equal(1, file.Mentions["other"]);
    }

    /// <summary>
    /// THE SAME LENGTH RULE AS THE TEXT READER'S: a name of one or two characters is a loop variable or
    /// a type parameter far more often than a declaration another file is built on.
    /// </summary>
    [Fact]
    public void ShortNames_AreNotMentions()
    {
        var file = Read("a.go", "package a\n\nfunc f() { g(); ab() }\n");
        Assert.Empty(file.Mentions);
    }
}
