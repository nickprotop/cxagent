using System.Text.Json;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Mcp.Auth;

/// <summary>
/// Access and refresh tokens, per MCP server, on disk at 0600.
///
/// <para>ITS OWN FILE, NEVER config.json. Config is the thing users paste into issues, commit to
/// dotfiles and screen-share — the exact reason placeholders exist for API keys there. A token
/// obtained through a browser login was never typed by the user and has no business appearing in a
/// file they treat as shareable. <c>permissions.json</c> beside it is the precedent: same directory,
/// same mode, separate concern.</para>
///
/// <para>BEST-EFFORT THROUGHOUT, like <see cref="PermissionRulesStore"/> and
/// <see cref="SqliteSessionStore"/>. An unreadable token file means "you are not logged in", which is
/// recoverable by logging in again; throwing would take down a session over a cache.</para>
/// </summary>
public sealed class TokenStore
{
    private readonly string _path;

    public TokenStore(AppPaths paths) =>
        _path = Path.Combine(paths.ConfigDir, "mcp-tokens.json");

    private sealed record Entry(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt);

    /// <summary>The tokens for a server, or null when there are none.</summary>
    public OAuthTokens? Get(string server)
    {
        var all = Load();
        return all.TryGetValue(server, out var e)
            ? new OAuthTokens(e.AccessToken, e.RefreshToken, e.ExpiresAt) : null;
    }

    /// <summary>Stores (or replaces) a server's tokens.</summary>
    public void Save(string server, OAuthTokens tokens)
    {
        var all = Load();
        all[server] = new Entry(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt);
        Write(all);
    }

    /// <summary>Forgets a server's tokens — what logging out means.</summary>
    public void Remove(string server)
    {
        var all = Load();
        if (all.Remove(server)) Write(all);
    }

    /// <summary>Which servers we hold tokens for, for <c>/mcp</c> to report.</summary>
    public IReadOnlyCollection<string> Servers() => Load().Keys;

    private Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path))
                ?? new(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Corrupt or unreadable reads as "not logged in" — recoverable by logging in again,
            // where throwing would take the session down over a cache.
            return new(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, Entry> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // ATOMIC, and 0600 BEFORE the file is in place. Chmodding after the move leaves a window
            // in which a world-readable file holds live tokens — short, but on a shared machine that
            // is the whole exposure.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            ChmodOwnerOnly(tmp);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception) { /* best effort: a failed save means logging in again, not a crash */ }
    }

    private static void ChmodOwnerOnly(string file)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) { /* a filesystem that rejects chmod must not break login */ }
    }
}
