using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

public class PluginEntriesTests
{
    private static PluginEntries Two() => new(new Dictionary<string, PluginConfig>
    {
        ["csharp-lsp"] = new("csharp-lsp.dll"),
        ["csharp-lsp-omnisharp"] = new("csharp-lsp.dll", Enabled: false),
    });

    /// <summary>
    /// WITH RETURNS A NEW SET, leaving the old one untouched. Every session holds a reference, and a
    /// mutator that edited in place would move views nobody asked to move — including a session
    /// mid-turn, whose model is reading the tool list right now.
    /// </summary>
    [Fact]
    public void WithLeavesTheOriginalAlone()
    {
        var before = Two();

        var after = before.With("csharp-lsp", new PluginConfig("csharp-lsp.dll", Enabled: false));

        Assert.True(before.All["csharp-lsp"].Enabled);
        Assert.False(after.All["csharp-lsp"].Enabled);
    }

    /// <summary>
    /// TWO NAMES MAY SHARE ONE BINARY — config.sample.json ships exactly that. Keying on the name is
    /// what keeps them separable; keying on the file would collapse two entries a user made on purpose.
    /// </summary>
    [Fact]
    public void EntriesAreKeyedByNameNotFile()
    {
        var entries = Two().With("csharp-lsp", new PluginConfig("csharp-lsp.dll", Enabled: false));

        Assert.False(entries.All["csharp-lsp"].Enabled);
        Assert.False(entries.All["csharp-lsp-omnisharp"].Enabled);   // untouched, already false
        Assert.Equal(2, entries.All.Count);
    }

    /// <summary>Removing one name leaves the other, for the same reason.</summary>
    [Fact]
    public void WithoutRemovesOneNameOnly()
    {
        var after = Two().Without("csharp-lsp");

        Assert.False(after.All.ContainsKey("csharp-lsp"));
        Assert.True(after.All.ContainsKey("csharp-lsp-omnisharp"));
    }

    /// <summary>Removing a name that is not there is not an error — the caller's goal is already true.</summary>
    [Fact]
    public void WithoutAnAbsentNameIsAnEmptyChange()
    {
        var after = Two().Without("never-configured");

        Assert.Equal(2, after.All.Count);
    }
}
