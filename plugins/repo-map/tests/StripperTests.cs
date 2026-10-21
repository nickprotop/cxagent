using CxAgent.Plugins.RepoMap;
using Xunit;

namespace CxAgent.Plugins.RepoMap.Tests;

public class StripperTests
{
    /// <summary>
    /// THE TWO OUTPUTS LINE UP, CHARACTER FOR CHARACTER. The outliner finds a declaration's end in
    /// one and quotes it from the other at the same column, so any drift cuts a signature in the
    /// wrong place.
    /// </summary>
    [Fact]
    public void CodeAndDisplay_HaveTheSameShape()
    {
        var text = "var a = \"x { y\"; // note {\r\n/* block\n { */ var b = @\"q\"\"{\";\n";
        var stripped = Stripper.Strip(text, Language.CSharp);

        Assert.Equal(stripped.Code.Length, stripped.Display.Length);
        for (var i = 0; i < stripped.Code.Length; i++)
            Assert.Equal(stripped.Code[i].Length, stripped.Display[i].Length);
    }

    [Fact]
    public void StringContents_AreBlankedInCode_AndKeptInDisplay()
    {
        var stripped = Stripper.Strip("f(\", \") // { comment", Language.CSharp);

        Assert.Equal("f(\"  \")", stripped.Code[0].TrimEnd());
        Assert.Equal("f(\", \")", stripped.Display[0].TrimEnd());
        Assert.DoesNotContain("{", stripped.Code[0]);
        Assert.DoesNotContain("comment", stripped.Display[0]);
    }

    /// <summary>A VERBATIM STRING'S DOUBLED QUOTE is one character of it, not its end.</summary>
    [Fact]
    public void AVerbatimString_EndsAtItsRealQuote()
    {
        var stripped = Stripper.Strip("var s = @\"a\"\"{\"; class X {", Language.CSharp);
        Assert.Contains("class X {", stripped.Code[0]);
        Assert.Equal(1, stripped.Code[0].Count(c => c == '{'));
    }

    /// <summary>A STRING THE STRIPPER MISREADS ENDS AT ITS LINE, so one odd line costs one line.</summary>
    [Fact]
    public void AnUnterminatedString_EndsAtTheLine()
    {
        var stripped = Stripper.Strip("var s = \"never closed\nclass Next {", Language.CSharp);
        Assert.Equal("class Next {", stripped.Code[1]);
    }

    [Fact]
    public void PythonTripleQuotes_SpanLines()
    {
        var stripped = Stripper.Strip("x = '''\ndef fake():\n'''\ndef real():", Language.Python);
        Assert.DoesNotContain("fake", stripped.Code[1]);
        Assert.Equal("def real():", stripped.Code[3]);
    }

    /// <summary>IN SHELL, <c>$#</c> IS THE ARGUMENT COUNT, not the start of a comment.</summary>
    [Fact]
    public void Shell_DollarHash_IsNotAComment()
    {
        var stripped = Stripper.Strip("n=$# ; f() { # real comment", Language.Shell);
        Assert.Contains("f() {", stripped.Code[0]);
        Assert.DoesNotContain("real", stripped.Code[0]);
    }
}
