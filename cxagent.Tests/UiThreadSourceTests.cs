using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Commands typed on the UI thread that wait on processes run that waiting on the pool.
///
/// <para>A SOURCE CHECK, as PromptStaysPutTests is one: the stall it guards against needs a live stdio server that
/// ignores its stdin closing, and the rule it pins is where the reload runs. Disposing each old server waits up to a
/// second for it to exit; on the UI thread, that is a second of frozen window per server.</para>
/// </summary>
public class UiThreadSourceTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }

    [Fact]
    public void TheReload_RunsOnThePool()
    {
        var source = File.ReadAllText(Path.Combine(Repo(), "cxagent", "UI", "McpCommand.cs"));

        Assert.Contains("await Task.Run(() => mcp.ReloadAsync(", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// /tasks stop, TYPED, STOPS ON THE POOL. Each killed shell is waited for — up to five seconds when a process is
    /// stuck — and the command is typed on the UI thread. The panel's x and the tab's Stop already run there.
    /// </summary>
    [Fact]
    public void TasksStop_RunsOnThePool()
    {
        var source = File.ReadAllText(Path.Combine(Repo(), "cxagent", "UI", "AppBootstrap.cs"));

        Assert.Contains("Task.Run(() => current.StopTask(arguments))", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// DELETING AN AGENT AND STOPPING ITS COMMANDS KILLS THEM ON THE POOL: each kill is waited for, and the delete
    /// question's answer arrives on the UI thread.
    /// </summary>
    [Fact]
    public void ADeleteThatStopsCommands_RunsOnThePool()
    {
        var source = File.ReadAllText(Path.Combine(Repo(), "cxagent", "UI", "MainWindow.cs"));

        Assert.Contains("await Task.Run(() => session.ForgetAgent(", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// /plugin load OF A NATIVE PLUGIN LAUNCHES ITS HOST ON THE POOL. A host that fails is disposed by waiting up to two
    /// seconds for it to exit, and the load is typed on the UI thread.
    /// </summary>
    [Fact]
    public void ANativePluginLoad_RunsOnThePool()
    {
        var source = File.ReadAllText(Path.Combine(Repo(), "cxagent.Core", "Core", "Sessions", "Session.cs"));

        Assert.Contains("await Task.Run(() => CxAgent.Core.Plugins.Abi.AbiPluginLoader.Load(", source,
            StringComparison.Ordinal);
    }
}
