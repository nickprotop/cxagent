using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The EDITOR's pure decisions, not the dialog. The flow needs a live window; what it decides does
/// not — the same split <see cref="ProviderCatalogEditorTests"/> already makes.
/// </summary>
public class McpServerEditorTests
{
    private static ProviderSettings Settings(params (string Name, string[] Cmd)[] servers) =>
        new(new Dictionary<string, ProviderInstanceConfig>(), null,
            Array.Empty<string>(), new Dictionary<string, RoutingTarget>())
        {
            McpServers = servers.ToDictionary(s => s.Name, s => new McpServerConfig(s.Cmd)),
        };

    [Fact]
    public void AddOrReplace_AddsAServer_AndKeepsTheRest()
    {
        var before = Settings(("existing", ["python3", "-m", "srv"]));

        var after = McpServerEditor.AddOrReplace(before, "added", new McpServerConfig(["npx", "thing"]));

        Assert.Equal(2, after.McpServers.Count);
        Assert.Equal(["npx", "thing"], after.McpServers["added"].Command);
        Assert.Equal(["python3", "-m", "srv"], after.McpServers["existing"].Command);
    }

    /// <summary>Same name overwrites rather than duplicating — two servers with one name is a config
    /// whose tool names collide.</summary>
    [Fact]
    public void AddOrReplace_SameName_Overwrites()
    {
        var before = Settings(("srv", ["old"]));

        var after = McpServerEditor.AddOrReplace(before, "srv", new McpServerConfig(["new"]));

        Assert.Equal(["new"], Assert.Single(after.McpServers).Value.Command);
    }

    [Fact]
    public void RemoveServer_LeavesTheOthersAlone()
    {
        var before = Settings(("keep", ["a"]), ("drop", ["b"]));

        var after = McpServerEditor.RemoveServer(before, "drop");

        var only = Assert.Single(after.McpServers);
        Assert.Equal("keep", only.Key);
        Assert.Equal(["a"], only.Value.Command);
    }

    /// <summary>Removing something that is not there changes nothing, rather than throwing.</summary>
    [Fact]
    public void RemoveServer_ThatIsNotThere_IsANoOp()
    {
        var before = Settings(("keep", ["a"]));

        Assert.Single(McpServerEditor.RemoveServer(before, "ghost").McpServers);
    }

    /// <summary>
    /// A server can be disabled without deleting it. The common case is "not now", and having to
    /// retype an npx command line from memory is exactly why people never switch one back on.
    /// </summary>
    [Fact]
    public void SetEnabled_FlipsTheFlag_WithoutLosingTheCommand()
    {
        var before = Settings(("srv", ["npx", "-y", "some-server"]));

        var off = McpServerEditor.SetEnabled(before, "srv", false);
        Assert.False(off.McpServers["srv"].Enabled);
        Assert.Equal(["npx", "-y", "some-server"], off.McpServers["srv"].Command);

        var on = McpServerEditor.SetEnabled(off, "srv", true);
        Assert.True(on.McpServers["srv"].Enabled);
        Assert.Equal(["npx", "-y", "some-server"], on.McpServers["srv"].Command);
    }

    /// <summary>An empty command is rejected in the editor, where the user can fix it, rather than at
    /// load time on the next launch — where it becomes a warning about a server that never appears.</summary>
    [Fact]
    public void Validate_RejectsAnEmptyCommand()
    {
        Assert.NotNull(McpServerEditor.Validate(""));
        Assert.NotNull(McpServerEditor.Validate("   "));
        Assert.NotNull(McpServerEditor.Validate(null));
        Assert.Null(McpServerEditor.Validate("npx -y some-server"));
    }

    /// <summary>
    /// A command line splits on whitespace into argv, with NO quote handling.
    ///
    /// <para>argv goes straight to the process. Quoting rules here would imply a shell we
    /// deliberately do not run, and the difference shows up as an argument arriving with its quotes
    /// still attached.</para>
    /// </summary>
    [Fact]
    public void ParseCommand_SplitsOnWhitespace_AndDropsEmptyRuns()
    {
        Assert.Equal(["npx", "-y", "@scope/pkg", "/tmp"],
                     McpServerEditor.ParseCommand("  npx   -y  @scope/pkg /tmp  "));
    }

    /// <summary>A row names the server and shows its command, so the list is readable without
    /// opening anything.</summary>
    [Fact]
    public void DescribeRows_ShowsTheNameAndCommand()
    {
        var row = Assert.Single(McpServerEditor.DescribeRows(Settings(("files", ["npx", "-y", "fs"]))));

        Assert.Equal("files", row.Name);
        Assert.Contains("files", row.Line, StringComparison.Ordinal);
        Assert.Contains("npx -y fs", row.Line, StringComparison.Ordinal);
    }

    /// <summary>A disabled server says so on its row — otherwise it looks configured and working,
    /// which is the state someone opens Settings to check.</summary>
    [Fact]
    public void DescribeRows_MarksADisabledServer()
    {
        var settings = McpServerEditor.SetEnabled(Settings(("off", ["x"])), "off", false);

        Assert.Contains("disabled", Assert.Single(McpServerEditor.DescribeRows(settings)).Line,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The name is kept BESIDE the line rather than parsed back out of it. A server whose name
    /// contains the row separator would otherwise be recovered wrongly — the same trap
    /// ProviderCatalogEditor.DescribeRows avoids.
    /// </summary>
    [Fact]
    public void DescribeRows_KeepsTheNameSeparately_EvenWhenItContainsTheSeparator()
    {
        var row = Assert.Single(McpServerEditor.DescribeRows(Settings(("a — b", ["cmd"]))));

        Assert.Equal("a — b", row.Name);
    }
}
