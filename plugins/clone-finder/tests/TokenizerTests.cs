using CxAgent.Plugins.CloneFinder;
using Xunit;

namespace CxAgent.Plugins.CloneFinder.Tests;

public class TokenizerTests
{
    /// <summary>
    /// THE RENAMED COPY IS THE POINT. Two loops differing only in identifier names normalise to
    /// the same stream, which is what lets the detector see them as one clone.
    /// </summary>
    [Fact]
    public void RenamedIdentifiersNormaliseTheSame()
    {
        var a = Tokenizer.Normalise("for (int i = 0; i < n; i++) sum += a[i];");
        var b = Tokenizer.Normalise("for (int j = 0; j < m; j++) tot += b[j];");

        Assert.Equal(a.Select(t => t.Text), b.Select(t => t.Text));
    }

    /// <summary>
    /// LITERALS ARE KEPT, so two assignments with different constants are NOT the same block.
    /// Folding them too would match any assignment to anything.
    /// </summary>
    [Fact]
    public void DifferentLiteralsDoNotNormaliseTheSame()
    {
        var a = Tokenizer.Normalise("Timeout = 30;");
        var b = Tokenizer.Normalise("Retries = 5;");

        Assert.NotEqual(a.Select(t => t.Text), b.Select(t => t.Text));
    }

    [Fact]
    public void CommentsAndWhitespaceAreDropped()
    {
        var a = Tokenizer.Normalise("x = 1; // set it\n");
        var b = Tokenizer.Normalise("x=1;\n\n  /* set it */\n");

        Assert.Equal(a.Select(t => t.Text), b.Select(t => t.Text));
    }

    /// <summary>A keyword is not an identifier: folding `for` would make every loop match every
    /// other statement of the same length.</summary>
    [Fact]
    public void KeywordsSurviveFolding()
    {
        var tokens = Tokenizer.Normalise("for (x = 1;)");
        Assert.Contains(tokens, t => t.Text == "for");
    }

    /// <summary>A token carries the line it came from, or a match cannot be reported as a
    /// location.</summary>
    [Fact]
    public void TokensCarryTheirLine()
    {
        var tokens = Tokenizer.Normalise("a;\nb;\nc;");
        Assert.Equal([1, 2, 3], tokens.Where(t => t.Text == "_").Select(t => t.Line));
    }

    /// <summary>A string containing // is not a comment, and a quote inside a comment does not
    /// open a string. Getting either wrong silently corrupts every later comparison.</summary>
    [Fact]
    public void StringsAndCommentsDoNotConfuseEachOther()
    {
        var withUrl = Tokenizer.Normalise("""x = "http://a";""");
        Assert.Contains(withUrl, t => t.Text.Contains("http://a"));

        var quoteInComment = Tokenizer.Normalise("""x = 1; // don't""");
        Assert.Equal(["_", "=", "1", ";"], quoteInComment.Select(t => t.Text));
    }
}
