namespace CxAgent.Core.Sandbox;

/// <summary>
/// Which variables a contained command does not get.
///
/// <para>THE FILESYSTEM HIDE IS HALF OF IT. <c>~/.aws</c> seen empty means little while
/// <c>AWS_SECRET_ACCESS_KEY</c> is still in the environment, and with the network open a command allowed without
/// asking can send <c>env</c> anywhere. So credential-shaped names go, the session's socket addresses go (their
/// sockets are hidden anyway), and so does every variable cxagent's own config reads a key from.</para>
///
/// <para>PATTERNS, NOT A LIST OF PRODUCTS. A list of known services is out of date the day it ships; the shapes
/// people name secrets in are not.</para>
/// </summary>
public static class SandboxEnv
{
    /// <summary>Removed whatever their name looks like: their sockets are hidden, and a stale address only
    /// confuses the program that reads it.</summary>
    private static readonly string[] Sockets =
        ["DBUS_SESSION_BUS_ADDRESS", "SSH_AUTH_SOCK", "TMUX", "TMUX_PANE", "DISPLAY", "WAYLAND_DISPLAY"];

    /// <summary>The shapes credentials are named in.</summary>
    private static readonly string[] CredentialPatterns =
        ["*_API_KEY", "*_APIKEY", "*_KEY", "*_TOKEN", "*_SECRET*", "*PASSWORD*", "*_PWD", "*_CREDENTIALS", "AWS_*",
         "DATABASE_URL"];

    /// <summary>The names to unset, in the environment's own order.</summary>
    /// <param name="environment">The environment the command would inherit.</param>
    /// <param name="settings">The user's additions (<see cref="SandboxSettings.HiddenEnv"/>) and exceptions
    /// (<see cref="SandboxSettings.PassEnv"/>).</param>
    /// <param name="configKeyVariables">The variables config.json reads a value from with <c>{env:NAME}</c>.</param>
    public static IReadOnlyList<string> ToUnset(IReadOnlyDictionary<string, string> environment,
        SandboxSettings settings, IReadOnlyCollection<string> configKeyVariables)
    {
        var patterns = CredentialPatterns.Concat(settings.HiddenEnv).ToList();
        return [.. environment.Keys.Where(name =>
            !settings.PassEnv.Contains(name, StringComparer.OrdinalIgnoreCase)
            && (Sockets.Contains(name, StringComparer.Ordinal)
                || configKeyVariables.Contains(name, StringComparer.Ordinal)
                || patterns.Any(p => Matches(p, name))))];
    }

    /// <summary>A <c>*</c>-only glob, ignoring case: <c>github_token</c> is the same secret as <c>GITHUB_TOKEN</c>.</summary>
    internal static bool Matches(string pattern, string name)
    {
        var parts = pattern.Split('*');
        if (parts.Length == 1) return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
        if (!name.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) return false;
        var at = parts[0].Length;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var found = name.IndexOf(parts[i], at, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            at = found + parts[i].Length;
        }
        var last = parts[^1];
        return name.Length - at >= last.Length && name.EndsWith(last, StringComparison.OrdinalIgnoreCase);
    }
}
