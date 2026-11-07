using System.Text.Json;
using CxAgent.Core.Sandbox;

namespace CxAgent.Core.Web;

/// <summary>Who must log in to the web server — see <see cref="WebSettings.Login"/>.</summary>
public enum LoginMode
{
    /// <summary>Required when the server listens beyond this machine; not on loopback.</summary>
    Auto,

    /// <summary>Required everywhere, loopback included — for a machine other people log in to.</summary>
    Always,

    /// <summary>Never. Allowed beyond this machine, and said loudly every time it is.</summary>
    Off,
}

/// <summary>
/// How cxagent serves browsers: where it listens, who may log in, and which folders a browser may open.
///
/// <para>USER CONFIG ONLY, like the sandbox. A cloned repository that could add a user or widen the roots
/// would be handing itself a login to the machine it was cloned onto.</para>
///
/// <para>PATHS ARE HELD AS WRITTEN (<c>~/source</c> stays <c>~/source</c>) and expanded on the machine the
/// server runs on, so one config file serves a user whose home is somewhere else.</para>
/// </summary>
/// <param name="Port">The TCP port to listen on.</param>
/// <param name="Bind">The address to listen on. Loopback keeps the server to this machine.</param>
/// <param name="SelfSigned">Serve HTTPS with a certificate generated once and kept in the config folder.</param>
/// <param name="Login">Who must log in — see <see cref="LoginMode"/>.</param>
/// <param name="Users">User name to password: plain, or a hash starting <c>pbkdf2:</c>.</param>
/// <param name="Roots">Folders a browser may open sessions under. Empty means the folder the server started in.</param>
/// <param name="IdleMinutes">How long a session nobody is looking at stays loaded. Zero keeps it loaded.</param>
public sealed record WebSettings(
    int Port,
    string Bind,
    bool SelfSigned,
    LoginMode Login,
    IReadOnlyDictionary<string, string> Users,
    IReadOnlyList<string> Roots,
    int IdleMinutes)
{
    /// <summary>What a browser may do beyond the conversation: see <see cref="WebAccess"/>.</summary>
    public WebAccess Allow { get; init; } = WebAccess.Default;

    /// <summary>What config.json without a <c>web</c> block means: this machine only, port 5180, no users.</summary>
    public static readonly WebSettings Default =
        new(5180, "127.0.0.1", false, LoginMode.Auto, new Dictionary<string, string>(), [], 30);

    /// <summary>
    /// Reads config.json's <c>web</c> block. Absent is <see cref="Default"/>; a field of the wrong shape warns and
    /// keeps its default, so one typo does not throw away the rest of the block.
    /// </summary>
    /// <param name="root">The config document's root object.</param>
    /// <param name="warnings">Where a shape problem is reported.</param>
    public static WebSettings Read(JsonElement root, List<string> warnings)
    {
        if (!root.TryGetProperty("web", out var block)) return Default;
        if (block.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("web must be an object — using the defaults.");
            return Default;
        }

        var d = Default;
        return new WebSettings(
            ReadPort(block, warnings, d.Port),
            Text(block, "bind", warnings, d.Bind),
            Flag(block, "selfSigned", warnings, d.SelfSigned),
            ReadLogin(block, warnings),
            ReadUsers(block, warnings),
            ReadRoots(block, warnings),
            Minutes(block, warnings, d.IdleMinutes))
        {
            Allow = ReadAllow(block, warnings),
        };
    }

    private static WebAccess ReadAllow(JsonElement block, List<string> warnings)
    {
        var d = WebAccess.Default;
        if (!block.TryGetProperty("allow", out var allow)) return d;
        if (allow.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("web.allow must be an object of switches — using the defaults.");
            return d;
        }
        return new WebAccess(
            Flag(allow, "shell", warnings, d.Shell, "web.allow"),
            Flag(allow, "config", warnings, d.Config, "web.allow"),
            Flag(allow, "plugins", warnings, d.Plugins, "web.allow"),
            Flag(allow, "openAnywhere", warnings, d.OpenAnywhere, "web.allow"));
    }

    private static int ReadPort(JsonElement block, List<string> warnings, int fallback)
    {
        if (!block.TryGetProperty("port", out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var port) && port is >= 1 and <= 65535) return port;
        warnings.Add($"web.port must be a number from 1 to 65535 — using {fallback}.");
        return fallback;
    }

    private static int Minutes(JsonElement block, List<string> warnings, int fallback)
    {
        if (!block.TryGetProperty("idleMinutes", out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var minutes) && minutes >= 0) return minutes;
        warnings.Add($"web.idleMinutes must be a whole number, 0 or more — using {fallback}.");
        return fallback;
    }

    private static string Text(JsonElement block, string name, List<string> warnings, string fallback)
    {
        if (!block.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) return v.GetString()!;
        warnings.Add($"web.{name} must be a non-empty string — using {fallback}.");
        return fallback;
    }

    private static bool Flag(JsonElement block, string name, List<string> warnings, bool fallback, string under = "web")
    {
        if (!block.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        warnings.Add($"{under}.{name} must be true or false — using {(fallback ? "true" : "false")}.");
        return fallback;
    }

    private static LoginMode ReadLogin(JsonElement block, List<string> warnings)
    {
        if (!block.TryGetProperty("login", out var v)) return LoginMode.Auto;
        if (v.ValueKind == JsonValueKind.String
            && Enum.TryParse<LoginMode>(v.GetString(), ignoreCase: true, out var mode)
            && Enum.IsDefined(mode))
            return mode;

        // AUTO, NOT OFF. A mistyped mode must not be the thing that opens the server to anyone.
        warnings.Add("web.login must be \"auto\", \"always\" or \"off\" — using auto.");
        return LoginMode.Auto;
    }

    private static Dictionary<string, string> ReadUsers(JsonElement block, List<string> warnings)
    {
        var users = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!block.TryGetProperty("users", out var v)) return users;
        if (v.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("web.users must be an object of user name to password — ignored.");
            return users;
        }
        foreach (var user in v.EnumerateObject())
        {
            if (user.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(user.Value.GetString()))
                users[user.Name] = user.Value.GetString()!;
            else
                warnings.Add($"web.users: '{user.Name}' needs a password string — that user is ignored.");
        }
        return users;
    }

    private static List<string> ReadRoots(JsonElement block, List<string> warnings)
    {
        var roots = new List<string>();
        if (!block.TryGetProperty("roots", out var v)) return roots;
        if (v.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("web.roots must be a list of folders — ignored.");
            return roots;
        }
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString())) continue;
            var path = e.GetString()!;

            // A RELATIVE ROOT WOULD RESOLVE AGAINST WHEREVER THE SERVER WAS STARTED — a different set of folders
            // each launch — so it is refused out loud rather than guessed at.
            if (SandboxSettings.IsAnchored(path)) roots.Add(path);
            else warnings.Add($"web.roots: '{path}' is relative — use a path starting with / or ~. Ignored.");
        }
        return roots;
    }
}

/// <summary>
/// What a browser may do beyond the conversation — the terminal's own tools that reach further than the agent does.
///
/// <para>EACH ITS OWN SWITCH, because the risks differ: a shell runs as you outside the sandbox and its prompts, a
/// config edit can change providers and their keys, a plugin is code you install, and an editor beyond the web roots
/// reads and writes any file you can. Wanting the editor on a phone is no reason to let it rewrite config.</para>
///
/// <para>NEVER CHANGED FROM A BROWSER. Even with <see cref="Config"/> on, the page shows these, the users and the
/// roots read-only: a login must not be able to widen its own access.</para>
/// </summary>
/// <param name="Shell">A real terminal in a browser tab — <c>/shell</c>.</param>
/// <param name="Config">The configuration page — the terminal's F8, and the commands that write config.json.</param>
/// <param name="Plugins">The plugin marketplace — F2 and <c>/plugin</c>. Each plugin still asks before it loads.</param>
/// <param name="OpenAnywhere">The editor beyond the web roots: any path the terminal's <c>/open</c> accepts.</param>
public sealed record WebAccess(bool Shell, bool Config, bool Plugins, bool OpenAnywhere)
{
    /// <summary>Plugins on, as the marketplace's load prompt still guards each one; everything else off.</summary>
    public static readonly WebAccess Default = new(Shell: false, Config: false, Plugins: true, OpenAnywhere: false);
}
