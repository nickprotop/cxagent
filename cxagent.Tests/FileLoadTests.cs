using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class FileLoadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-fileload").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    [Fact]
    public void TryLoad_ReadsTextAndLanguage()
    {
        var p = Write("a.cs", "class A { }\n"u8.ToArray());

        var loaded = FileLoad.TryLoad(p, out var refusal);

        Assert.NotNull(loaded);
        Assert.Null(refusal);
        Assert.Equal("class A { }\n", loaded!.Text);
        Assert.Equal("cs", loaded.Language);
    }

    // THE SNAPSHOT IS WHAT MAKES A SAVE LOSSLESS. FileMutation.WriteAsync restores the BOM and the
    // line endings from it, so a file that had them must come back saying so.
    [Fact]
    public void TryLoad_RemembersBomAndCrlf()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("a\r\nb\r\n"u8.ToArray()).ToArray();
        var p = Write("b.txt", bytes);

        var loaded = FileLoad.TryLoad(p, out _);

        Assert.True(loaded!.Snapshot.HadBom);
        Assert.True(loaded.Snapshot.UsesCrlf);

        // And the BOM is not left in the text the editor shows.
        Assert.StartsWith("a", loaded.Text);
    }

    [Fact]
    public void TryLoad_RefusesBinary()
    {
        var p = Write("c.bin", new byte[] { 0x7F, 0x45, 0x00, 0x4C });

        var loaded = FileLoad.TryLoad(p, out var refusal);

        Assert.Null(loaded);
        Assert.Contains("binary", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    // AN UNREADABLE FILE COUNTS AS BINARY: the tab says why rather than throwing.
    [Fact]
    public void TryLoad_RefusesAMissingFile()
    {
        var loaded = FileLoad.TryLoad(Path.Combine(_dir, "nope.cs"), out var refusal);

        Assert.Null(loaded);
        Assert.NotNull(refusal);
    }
}
