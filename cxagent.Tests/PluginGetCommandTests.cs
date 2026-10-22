using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>/plugin get &lt;name&gt;</c> reads its name out of the verb's argument string.
/// </summary>
public class PluginGetCommandTests
{
    /// <summary>
    /// THE VERB TRAVELS WITH THE ARGUMENTS. RegisterVerb matches on the first word and hands the
    /// handler everything after the command name — including the verb — so a handler that took the
    /// string whole would look for a plugin called "get clone-finder".
    /// </summary>
    [Fact]
    public void TheVerbIsNotPartOfTheName()
    {
        Assert.Equal("clone-finder", PluginGetCommand.NameFrom("get clone-finder"));
    }

    [Fact]
    public void ExtraSpacingIsTrimmed()
    {
        // A line copied off a web page can arrive with trailing whitespace.
        Assert.Equal("clone-finder", PluginGetCommand.NameFrom("get   clone-finder  "));
    }

    [Fact]
    public void AVerbWithNoName_IsEmptyRatherThanTheVerb()
    {
        // "/plugin get" alone must report usage, not try to install something called "get".
        Assert.Equal("", PluginGetCommand.NameFrom("get"));
        Assert.Equal("", PluginGetCommand.NameFrom("get "));
    }

    /// <summary>
    /// THE NEXT STEP NAMES A STORE PLUGIN BY ITS QUALIFIED NAME: its file alone never reaches into a
    /// store's folder, so "/plugin load repo-map.dll" would load the official copy, or nothing.
    /// </summary>
    [Fact]
    public void TheLoadHint_NamesAStorePluginByItsQualifiedName()
    {
        var entry = new CatalogEntry("repo-map", "repo-map", "1.0.0", "", "", "", "", "", "repo-map.dll", "managed", 2, [],
            null, null, null, null);
        Assert.Equal("/plugin load repo-map.dll", PluginGetCommand.LoadHint(entry));
        Assert.Equal("/plugin load acme/repo-map", PluginGetCommand.LoadHint(entry with { Store = "acme" }));
    }
}
