using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using SharpConsoleUI.Controls;

namespace CxAgent.UI;

/// <summary>
/// Everything <c>/mcp</c> does: list, inspect, reload, and log in.
///
/// <para>Lifted out of <see cref="AppBootstrap"/>, where it was two local functions closing over six
/// pieces of session state. That file is the composition root — its job is deciding what exists and
/// in what order — and a command's behaviour is not that. The dependencies were already explicit
/// enough to be constructor parameters; being captured by a closure only hid that.</para>
///
/// <para>The <c>/mcp reload</c> path is why this needs <see cref="AppPaths"/> and the environment: a
/// reload RE-READS config from disk rather than reusing the startup resolution, because picking up a
/// change made since launch is the entire point.</para>
/// </summary>
public sealed class McpCommand(
    Core.Mcp.McpManager mcp,
    Core.Mcp.Auth.TokenStore tokens,
    HttpClient http,
    AppPaths paths,
    IReadOnlyDictionary<string, string> env,
    MainWindow window)
{
    // /mcp, including the reload that makes config LIVE.
    public async Task HandleAsync(string arguments)
    {
        if (SessionCommands.ArgumentWords("/mcp " + arguments) is [var first, ..]
            && first.Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            window.Chat.AddMessage(ChatRole.System, "Reloading MCP servers…");

            // RE-READ FROM DISK, not from the startup resolution. The whole point is to pick up
            // a change made since launch — in Settings, or by hand-editing config.json.
            IReadOnlyDictionary<string, McpServerConfig> configured;
            try
            {
                configured = ProviderConfigLoader.LoadAndValidate(paths, env).McpServers;
            }
            catch (ProviderConfigException ex)
            {
                // An unrelated config error must not silently leave the old servers in place
                // without saying why the reload did nothing.
                window.Chat.AddMessage(ChatRole.System, 
                    $"[yellow]config.json could not be read: {string.Join("; ", ex.Errors)}[/]");
                return;
            }

            await mcp.ReloadAsync(configured, CancellationToken.None);

            foreach (var message in mcp.Messages.Concat(mcp.Toolset.Warnings))
                window.Chat.AddMessage(ChatRole.System, $"[yellow]{message}[/]");

            window.SetMcpServers(mcp.Statuses());
        }

        if (SessionCommands.ArgumentWords("/mcp " + arguments) is [var verb, var target, ..]
            && verb.Equals("login", StringComparison.OrdinalIgnoreCase))
        {
            await LoginAsync(target);
            return;
        }

        window.Chat.AddMessage(ChatRole.System, SessionCommands.DescribeMcp(
            mcp.Statuses(), arguments, mcp.Toolset.Names().ToList()));
    }

    // THE ONLY PLACE A BROWSER OPENS, and only because the user typed /mcp login. A 401 during
    // a turn sets a status and stops; opening a browser on the agent's own initiative, while its
    // user may be away from the machine, asks for credentials at a moment nobody chose.
    async Task LoginAsync(string serverName)
    {
        if (mcp.AuthMetadataUrlFor(serverName) is not { } metadataUrl)
        {
            window.Chat.AddMessage(ChatRole.System, 
                $"[yellow]'{serverName}' has not asked for authorization. Only a remote server "
                + "that answered 401 can be logged in to — run /mcp to see the servers.[/]");
            return;
        }

        window.Chat.AddMessage(ChatRole.System, $"Opening a browser to log in to '{serverName}'…");

        var result = await Core.Mcp.Auth.McpLogin.RunAsync(
            http, tokens, serverName, metadataUrl,
            // A PUBLIC CLIENT with no secret: cxagent runs on the user's machine, where anything
            // shipped as a "secret" is readable by whoever has the binary. PKCE is what actually
            // protects the exchange. Dynamic registration (RFC 7591) is out of scope — a server
            // requiring a pre-registered id says so, and the error names it.
            clientId: "cxagent", clientSecret: null,
            openBrowser: url =>
            {
                if (!TryOpenBrowser(url))
                    // A headless or locked-down machine still gets its login: the URL is the
                    // whole of what a browser would have been handed.
                    window.Chat.AddMessage(ChatRole.System, $"Open this URL to continue:\n{url}");
            },
            ct: CancellationToken.None);

        window.Chat.AddMessage(ChatRole.System, 
            result.Succeeded ? result.Message : $"[yellow]{result.Message}[/]");

        if (!result.Succeeded) return;

        // RECONNECT, so the tools are usable NOW. A login that leaves the server still
        // unauthorized until the next restart is a login the user would reasonably think failed.
        window.Chat.AddMessage(ChatRole.System, "Reconnecting…");
        await mcp.ReloadAsync(mcp.Configured, CancellationToken.None);
        window.SetMcpServers(mcp.Statuses());
        window.Chat.AddMessage(ChatRole.System, SessionCommands.DescribeMcp(mcp.Statuses()));
    }

    /// <summary>
    /// Hands a URL to the platform's browser. False when there is none to hand it to.
    ///
    /// <para>UseShellExecute is what makes the OS resolve the default handler; without it this tries
    /// to EXECUTE the URL as a program and fails on every platform. A headless box, a container or a
    /// locked-down desktop has no handler at all, which is not an error — the caller prints the URL
    /// instead, and that is the whole of what a browser would have received.</para>
    /// </summary>
    private static bool TryOpenBrowser(string url)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return p is not null;
        }
        catch (Exception) { return false; }
    }
}
