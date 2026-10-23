using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A folder typed into the Sources view: what is refused, and what is only flagged.</summary>
public class PluginFoldersTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ANothing_IsRefused(string typed) =>
        Assert.Equal("Type a folder to add.", PluginFolders.Problem(typed, []));

    /// <summary>LISTED TWICE SEARCHES NOTHING MORE — and the second copy would only ever be a row to remove.</summary>
    [Fact]
    public void AFolderAlreadyListed_IsRefused() =>
        Assert.Equal("~/src/plugins is listed already.", PluginFolders.Problem(" ~/src/plugins ", ["~/src/plugins"]));

    /// <summary>
    /// A FOLDER THAT DOES NOT EXIST IS ACCEPTED, and its row says so: a relative one resolves against whichever
    /// project is open, so "missing here" is not "missing", and one made later starts being searched.
    /// </summary>
    [Fact]
    public void AFolderThatDoesNotExist_IsAccepted() =>
        Assert.Null(PluginFolders.Problem("no/such/folder", []));

    [Fact]
    public void Trimmed_IsWhatIsKept() =>
        Assert.Equal("~/src/plugins", PluginFolders.Clean("  ~/src/plugins  "));
}
