using CxAgent.Plugins.RepoMap.Parsing;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

public sealed class TreeReferenceTests : IDisposable
{
    private readonly Engine _engine = Engine.Open();

    private FileOutline Read(string path, string source) =>
        TreeOutliner.Outline(path, source, _engine.ReaderFor(Grammars.For(path)!)!, TreeOutliner.ParseBudget)!;

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

    /// <summary>
    /// AN IMPORT IS A USE. Guava's files reach Preconditions through <c>import static
    /// ….Preconditions.checkNotNull</c> and never write the class name again; without the import,
    /// the most-used class in the library would have no one using it.
    /// </summary>
    [Theory]
    [InlineData("A.java", "import static com.google.common.base.Preconditions.checkNotNull;\nimport com.google.common.base.Strings;\nclass A {}\n", "Preconditions,Strings")]
    [InlineData("a.rs", "use crate::sink::{Sink, SinkError};\nuse std::io::Read;\nfn f() {}\n", "Sink,SinkError,Read")]
    [InlineData("a.php", "<?php\nuse Illuminate\\Support\\Collection;\nfunction f() {}\n", "Collection")]
    [InlineData("a.scala", "import scala.collection.mutable.ListBuffer\nobject A\n", "ListBuffer")]
    public void Imports_AreMentions(string path, string source, string names)
    {
        var mentions = Read(path, source).Mentions.Keys;
        foreach (var name in names.Split(','))
            Assert.Contains(name, mentions);
    }

    /// <summary>
    /// AN ANNOTATION IS A LABEL, NOT A DEPENDENCY. Every guava file imports and applies
    /// <c>@GwtCompatible</c>; ranked by those uses, the annotation would outrank the classes the
    /// files are actually built from. It is listed in its own file, and links nothing.
    /// </summary>
    [Fact]
    public void AnAnnotationUsedEverywhere_DoesNotOutrankWhatTheFilesUse()
    {
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
        Assert.Contains("public @interface Marked", Read("Marked.java", "package p;\npublic @interface Marked {}\n").Symbols.Select(s => s.Signature));
    }
}
