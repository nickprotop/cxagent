using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class FileProbeTests
{
    // THE DOT IS THE WHOLE POINT. Path.GetExtension returns ".cs", and SyntaxHighlighters.For(".cs")
    // returns null while For("cs") resolves — so a pass-through would disable highlighting on every
    // file and look like a missing grammar rather than a bug here.
    [Theory]
    [InlineData("a/b/Program.cs", "cs")]
    [InlineData("x.JSON", "json")]
    [InlineData("cxagent.csproj", "csproj")]
    public void LanguageFor_StripsTheDot_AndLowercases(string path, string expected)
        => Assert.Equal(expected, FileProbe.LanguageFor(path));

    [Theory]
    [InlineData("Makefile")]
    [InlineData("noext")]
    public void LanguageFor_IsNullWithoutAnExtension(string path)
        => Assert.Null(FileProbe.LanguageFor(path));

    [Fact]
    public void LooksBinary_TrueOnANullByte()
        => Assert.True(FileProbe.LooksBinary(new byte[] { 0x48, 0x00, 0x49 }));

    [Fact]
    public void LooksBinary_FalseOnPlainText()
        => Assert.False(FileProbe.LooksBinary("using System;\n"u8.ToArray()));

    // An empty file is text — it opens as an empty buffer rather than being refused.
    [Fact]
    public void LooksBinary_FalseOnEmpty()
        => Assert.False(FileProbe.LooksBinary(ReadOnlySpan<byte>.Empty));
}

/// <summary>
/// The null-byte test is necessary and not sufficient: data with no zero byte still renders as
/// replacement characters, which is the outcome the check exists to prevent.
/// </summary>
public class FileProbeBinaryTests
{
    // 400 random bytes have no null byte roughly a fifth of the time — this is that case, and it
    // opened as garbage in a live drive before the decode check was added.
    [Fact]
    public void LooksBinary_TrueOnRandomBytesWithNoNullByte()
    {
        var random = new byte[400];
        new Random(20260903).NextBytes(random);
        for (var i = 0; i < random.Length; i++)
            if (random[i] == 0) random[i] = 1;   // the case the first test cannot catch

        Assert.True(FileProbe.LooksBinary(random));
    }

    // TEXT IS NOT REFUSED FOR BEING UNUSUAL. Accented Latin, CJK and emoji all decode cleanly.
    [Theory]
    [InlineData("héllo wörld — naïve café")]
    [InlineData("日本語のテキストです")]
    [InlineData("emoji 🎉 and more 🚀")]
    public void LooksBinary_FalseOnNonAsciiText(string text)
        => Assert.False(FileProbe.LooksBinary(System.Text.Encoding.UTF8.GetBytes(text)));

    // A stray invalid byte in otherwise good text is not enough to refuse the file.
    [Fact]
    public void LooksBinary_FalseOnMostlyTextWithOneBadByte()
    {
        var bytes = "using System;\nclass A { }\n"u8.ToArray().Concat(new byte[] { 0xFF }).ToArray();

        Assert.False(FileProbe.LooksBinary(bytes));
    }
}

/// <summary>What the editor is handed, and what it makes of it.</summary>
public class EditorContentTests
{
    // A TRAILING NEWLINE IS A TERMINATOR, NOT A LINE. "a\nb\n" is two lines; splitting on \n yields
    // three, the last empty — which is what puts a phantom line at the end of every well-formed text
    // file and makes the count one too many.
    [Fact]
    public void LineCount_DoesNotCountTheTrailingNewline()
    {
        Assert.Equal(2, CxAgent.UI.FileTab.LineCountForTest("name: test\nvalue: 42\n"));
        Assert.Equal(2, CxAgent.UI.FileTab.LineCountForTest("a\nb"));
        Assert.Equal(1, CxAgent.UI.FileTab.LineCountForTest("only\n"));
        Assert.Equal(0, CxAgent.UI.FileTab.LineCountForTest(""));
    }
}

/// <summary>Does the control hold exactly what it was given?</summary>
public class EditorRoundTripTests
{
    // WHAT GOES IN COMES OUT. A phantom leading line in the gutter would mean the control holds
    // something other than the file, and every line number after it is wrong.
    [Fact]
    public void TheEditorHoldsTheFileItWasGiven()
    {
        var text = "using System;\nclass Hello\n{\n}\n";

        var editor = new SharpConsoleUI.Builders.MultilineEditControlBuilder()
            .WithContent(text)
            .WithWrapMode(SharpConsoleUI.Controls.WrapMode.NoWrap)
            .WithLineNumbers()
            .Build();

        Assert.Equal(text, editor.GetContent());
        Assert.StartsWith("using System;", editor.GetContent());
    }
}
