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
