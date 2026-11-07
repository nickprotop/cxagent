using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Commands;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Core.Web;
using CxAgent.Hosting;
using CxAgent.UI;
using CxAgent.Web.Auth;

namespace CxAgent.Web;

/// <summary>What <c>cxagent web</c> was given on its command line; null means "as config.json says".</summary>
public sealed record WebCliOptions(int? Port, string? Bind, bool? SelfSigned, string? ConfigDir);

/// <summary>
/// <c>cxagent web</c> — the web server with no terminal — and <c>cxagent web passwd &lt;user&gt;</c>.
///
/// <para>ITS OWN PARSER, BEFORE THE TERMINAL'S. Like <c>plugin</c> and <c>store</c>, it is decided on the first word and
/// never reaches the TUI's command line, so its flags cannot collide with a session's.</para>
/// </summary>
public static class WebCli
{
    /// <summary>The flags after <c>web</c>; null with <paramref name="error"/> set when they do not parse.</summary>
    public static WebCliOptions? Parse(string[] args, out string? error)
    {
        int? port = null;
        string? bind = null, configDir = null;
        bool? selfSigned = null;
        for (var i = 0; i < args.Length; i++)
        {
            string? Value()
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[++i];
                return null;
            }

            switch (args[i])
            {
                case "--port":
                    if (Value() is not { } p || !int.TryParse(p, out var n) || n is < 1 or > 65535)
                    {
                        error = "--port needs a number from 1 to 65535.";
                        return null;
                    }
                    port = n;
                    break;
                case "--bind":
                    if ((bind = Value()) is null) { error = "--bind needs an address, such as 127.0.0.1 or 0.0.0.0."; return null; }
                    break;
                case "--config-dir":
                    if ((configDir = Value()) is null) { error = "--config-dir needs a folder."; return null; }
                    break;
                case "--self-signed":
                    selfSigned = true;
                    break;
                default:
                    error = $"unknown option '{args[i]}'. cxagent web takes --port, --bind, --self-signed and --config-dir.";
                    return null;
            }
        }
        error = null;
        return new WebCliOptions(port, bind, selfSigned, configDir);
    }

    /// <summary>config.json's settings with the command line's on top: a flag given wins.</summary>
    public static WebSettings Effective(WebSettings config, WebCliOptions flags) => config with
    {
        Port = flags.Port ?? config.Port,
        Bind = flags.Bind ?? config.Bind,
        SelfSigned = flags.SelfSigned ?? config.SelfSigned,
    };

    /// <summary>
    /// Writes a hash of <paramref name="password"/> for <paramref name="user"/> into config.json's <c>web.users</c>,
    /// leaving every other key as it was, and the file owner-only.
    /// </summary>
    public static void SetPassword(string configFile, string user, string password)
    {
        JsonObject root;
        if (File.Exists(configFile))
        {
            root = JsonNode.Parse(File.ReadAllText(configFile), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new InvalidOperationException("config.json is not a JSON object.");
        }
        else
        {
            root = [];
        }

        if (root["web"] is not JsonObject web) root["web"] = web = [];
        if (web["users"] is not JsonObject users) web["users"] = users = [];
        users[user] = PasswordCheck.Hash(password);

        // THROUGH A SIBLING FILE AND A RENAME, created owner-only: config.json holds API keys, and a half-written one
        // would lose every setting at once.
        var temp = configFile + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, configFile, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(configFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Runs <c>cxagent web …</c>; the exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "passwd") return Passwd(args[1..]);

        var options = Parse(args, out var error);
        if (options is null) return Fail(error!);

        var paths = new AppPaths(options.ConfigDir);
        paths.EnsureCreated();
        var resolution = Startup.Resolve(paths, Startup.Env(), useMock: false, instance: null);
        if (resolution is null || !resolution.HasProvider) return Fail("no model is configured. Run cxagent once to set one up.");
        foreach (var warning in resolution.Warnings ?? []) Console.Error.WriteLine($"cxagent: {warning}");

        var settings = Effective(resolution.Web, options);
        var stores = Startup.Stores(paths);
        var prompts = new PromptHub();
        SessionManager? manager = null;

        // THE GATE'S OWN NOTICES GO TO THE SESSION THAT ASKED — into its event log, where its tabs see them.
        var gate = PermissionDecider.WithPrompt(stores.Rules, (sessionId, message) =>
        {
            if (manager?.Sessions.FirstOrDefault(s => s.Id == sessionId) is { } session)
            {
                if (message.Severity == Severity.Info) session.Notify(message.Text);
                else session.NotifyWarning(message.Text);
            }
        }, prompts.AskPermissionAsync);

        var services = Startup.Services(new StartupServicesRequest(paths, stores, gate, new LogFileManager(paths), resolution));
        manager = services.Manager;
        manager.Holder = LockHolder.Web;
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        // SIGTERM STOPS IT THE SAME WAY — what `kill` and a service manager send. Without it the process ends at once,
        // with turns unsaved and every session's lock left to the kernel.
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                stop.Cancel();
            });

        try
        {
            await services.Mcp.ReloadAsync(resolution.McpServers, stop.Token);
            var composer = new SessionComposer(new ComposerServices(manager, paths, stores.Rules, gate, stores.History) { Prompts = prompts });
            var opener = new ComposerSessionOpener(composer, resolution, WorkingMode.Default);

            WebServerHost host;
            try
            {
                host = await WebServerHost.StartAsync(new WebHostRequest(paths, settings, manager, prompts, opener, Environment.CurrentDirectory)
                {
                    ConfigSaved = opener.Adopt,
                    McpReload = servers => services.Mcp.ReloadAsync(servers, CancellationToken.None),
                });
            }
            catch (WebStartRefused refused)
            {
                return Fail(refused.Message);
            }

            await using (host)
            {
                Console.WriteLine($"cxagent web: serving at {host.Url}");
                foreach (var warning in host.Warnings) Console.Error.WriteLine($"cxagent: {warning}");
                Console.WriteLine("Press Ctrl+C to stop.");
                try { await Task.Delay(Timeout.Infinite, stop.Token); }
                catch (OperationCanceledException) { }
                Console.WriteLine("cxagent web: stopping — running turns are cancelled and saved.");
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            manager.Dispose();
            try { await services.Mcp.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
        }
    }

    private static int Passwd(string[] args)
    {
        string? configDir = null;
        string? user = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config-dir" && i + 1 < args.Length) configDir = args[++i];
            else if (user is null && !args[i].StartsWith("--", StringComparison.Ordinal)) user = args[i];
            else return Fail($"unexpected '{args[i]}'. Usage: cxagent web passwd <user> [--config-dir <folder>]");
        }
        if (user is null) return Fail("Usage: cxagent web passwd <user> [--config-dir <folder>]");
        if (Console.IsInputRedirected) return Fail("cxagent web passwd reads the password from the terminal; run it in one.");

        var first = ReadHidden($"Password for {user}: ");
        var second = ReadHidden("Again: ");
        if (first.Length == 0) return Fail("an empty password is not a password.");
        if (first != second) return Fail("the two did not match. Nothing was changed.");

        var paths = new AppPaths(configDir);
        paths.EnsureCreated();
        SetPassword(Path.Combine(paths.ConfigDir, "config.json"), user, first);
        Console.WriteLine($"Saved a hash of {user}'s password to web.users in config.json.");
        return 0;
    }

    // NOTHING ECHOED, so the password is not left on screen or in a terminal's scrollback.
    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"cxagent web: {message}");
        return 2;
    }
}
