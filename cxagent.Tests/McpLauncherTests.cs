using CxAgent.Core.Llm;
using CxAgent.Core.Mcp;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Starting the configured fleet. Driven against real subprocesses, because the failures worth
/// catching here — a command that does not exist, a server that dies mid-handshake — are process
/// failures, and a fake would model our assumptions about them rather than the behaviour.
/// </summary>
public class McpLauncherTests : IDisposable
{
    private readonly List<string> _scripts = [];

    public void Dispose()
    {
        foreach (var s in _scripts)
            try { File.Delete(s); } catch (Exception) { /* best effort */ }
    }

    private static bool HavePython =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Any(d => File.Exists(Path.Combine(d, "python3")));

    /// <summary>A minimal server that answers the handshake and offers one tool.</summary>
    private string[] WorkingServer()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcplaunch-" + Guid.NewGuid().ToString("N") + ".py");
        _scripts.Add(path);
        File.WriteAllText(path, """
            import sys, json
            for line in sys.stdin:
                line = line.strip()
                if not line:
                    continue
                msg = json.loads(line)
                m = msg.get("method")
                if m == "initialize":
                    print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": {
                        "protocolVersion": "2024-11-05", "capabilities": {"tools": {}},
                        "serverInfo": {"name": "s", "version": "1"}}}), flush=True)
                elif m == "tools/list":
                    print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": {"tools": [
                        {"name": "go", "description": "Goes.", "inputSchema": {"type": "object"}}]}}),
                        flush=True)
            """);
        return ["python3", path];
    }

    private static Dictionary<string, McpServerConfig> Config(params (string Name, string[] Cmd)[] entries) =>
        entries.ToDictionary(e => e.Name, e => new McpServerConfig(e.Cmd));

    [Fact]
    public async Task StartAsync_StartsAnEnabledServer_AndListsItsTools()
    {
        if (!HavePython) return;

        var result = await McpLauncher.StartAsync(Config(("good", WorkingServer())), CancellationToken.None);

        var only = Assert.Single(result.Servers);
        Assert.Equal("good", only.Name);
        Assert.Single(only.Tools);
        Assert.Empty(result.Messages);
        await only.DisposeAsync();
    }

    /// <summary>
    /// A SERVER THAT WILL NOT START COSTS ITS OWN TOOLS AND NOTHING ELSE. This is the whole
    /// degradation contract: the app starts, the other servers work, and the user is TOLD.
    /// </summary>
    [Fact]
    public async Task StartAsync_OneBadServer_DoesNotStopTheOthers()
    {
        if (!HavePython) return;

        var result = await McpLauncher.StartAsync(
            Config(("broken", ["definitely-not-a-real-binary-xyz"]), ("good", WorkingServer())),
            CancellationToken.None);

        Assert.Equal("good", Assert.Single(result.Servers).Name);
        Assert.Contains(result.Messages, m => m.Contains("broken", StringComparison.Ordinal));

        foreach (var s in result.Servers) await s.DisposeAsync();
    }

    /// <summary>The failure names the SERVER and carries its own error text — "npx: command not
    /// found" is fixable in seconds; a generic "failed to start" is not.</summary>
    [Fact]
    public async Task StartAsync_AFailedServer_IsReportedByNameWithItsOwnError()
    {
        var result = await McpLauncher.StartAsync(
            Config(("ghost", ["definitely-not-a-real-binary-xyz"])), CancellationToken.None);

        Assert.Empty(result.Servers);
        var message = Assert.Single(result.Messages);
        Assert.Contains("ghost", message, StringComparison.Ordinal);
        Assert.True(message.Length > "MCP server 'ghost' did not start: ".Length,
            "the server's own error text should be carried, not just the fact of failure");
    }

    /// <summary>A disabled server is a deliberate off switch: not started, and not complained about.
    /// Reporting "skipped" for something the user switched off is noise.</summary>
    [Fact]
    public async Task StartAsync_SkipsADisabledServer_Silently()
    {
        var result = await McpLauncher.StartAsync(
            new Dictionary<string, McpServerConfig>
            {
                ["off"] = new(["definitely-not-a-real-binary-xyz"], Enabled: false),
            },
            CancellationToken.None);

        Assert.Empty(result.Servers);
        Assert.Empty(result.Messages);
    }

    /// <summary>No configured servers is the common case and must cost nothing.</summary>
    [Fact]
    public async Task StartAsync_WithNothingConfigured_DoesNothing()
    {
        var result = await McpLauncher.StartAsync(
            new Dictionary<string, McpServerConfig>(), CancellationToken.None);

        Assert.Empty(result.Servers);
        Assert.Empty(result.Messages);
    }

    /// <summary>
    /// Servers come back IN CONFIGURED ORDER, not in whichever order they finished starting.
    ///
    /// <para>The tool list derives from this, and the tool list is part of the prompt-cache prefix —
    /// so an order that depended on a startup race would change the prefix between runs of an
    /// unchanged config.</para>
    /// </summary>
    [Fact]
    public async Task StartAsync_ReturnsServersInConfiguredOrder()
    {
        if (!HavePython) return;

        var result = await McpLauncher.StartAsync(
            Config(("alpha", WorkingServer()), ("beta", WorkingServer()), ("gamma", WorkingServer())),
            CancellationToken.None);

        Assert.Equal(["alpha", "beta", "gamma"], result.Servers.Select(s => s.Name));

        foreach (var s in result.Servers) await s.DisposeAsync();
    }
}
