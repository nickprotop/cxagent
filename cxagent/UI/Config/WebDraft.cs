using System.Net;
using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using CxAgent.Core.Sandbox;
using CxAgent.Core.Web;
using CxAgent.Web.Auth;

namespace CxAgent.UI.Config;

/// <summary>One user as the Web tab shows it: the name, and whether config.json holds its password in plain text.</summary>
/// <param name="Name">The user name.</param>
/// <param name="Plain">True when the stored value is the password itself rather than a hash.</param>
public sealed record WebUser(string Name, bool Plain);

/// <summary>
/// The Web tab's settings, as edited — config.json's <c>web</c> block. Holds no UI, so what is read and what is written
/// can be tested without a window.
///
/// <para>WRITES ONLY WHAT CHANGED, like the General tab: a key at its default is removed, with the block when nothing is
/// left in it, and a key this tab does not know is kept.</para>
///
/// <para>A PASSWORD IS NEVER SHOWN AND ALWAYS STORED HASHED. A user's stored value is kept exactly as it is unless the
/// tab sets a new one — so a plain password already in the file stays plain until it is replaced, and is marked so.</para>
/// </summary>
public sealed class WebDraft
{
    private WebSettings _original = WebSettings.Default;
    private string _originalRoots = "";
    private Dictionary<string, string> _originalUsers = new(StringComparer.Ordinal);

    // WHAT THE FILE WILL HOLD PER USER: the stored value as read, or a hash just made from a typed password.
    private readonly Dictionary<string, string> _users = new(StringComparer.Ordinal);

    private WebDraft() { }

    /// <summary>The settings as config has them.</summary>
    public static WebDraft From(ProviderSettings settings)
    {
        var web = settings.Web ?? WebSettings.Default;
        var draft = new WebDraft
        {
            PortText = web.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Bind = web.Bind,
            SelfSigned = web.SelfSigned,
            Login = web.Login,
            RootsText = string.Join("\n", web.Roots),
            IdleText = web.IdleMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var (name, stored) in web.Users) draft._users[name] = stored;
        draft.Saved();
        return draft;
    }

    /// <summary>The port as typed.</summary>
    public string PortText { get; set; } = "5180";

    /// <summary>The address to listen on, as typed.</summary>
    public string Bind { get; set; } = "127.0.0.1";

    /// <summary>Serve HTTPS with a self-signed certificate.</summary>
    public bool SelfSigned { get; set; }

    /// <summary>Who must log in.</summary>
    public LoginMode Login { get; set; }

    /// <summary>The folders a browser may open sessions under, one per line as typed.</summary>
    public string RootsText { get; set; } = "";

    /// <summary>Minutes a session nobody looks at stays loaded, as typed.</summary>
    public string IdleText { get; set; } = "30";

    /// <summary>The users, in name order.</summary>
    public IReadOnlyList<WebUser> Users =>
        [.. _users.OrderBy(u => u.Key, StringComparer.Ordinal).Select(u => new WebUser(u.Key, !PasswordCheck.IsHash(u.Value)))];

    /// <summary>Adds a user with a password, stored hashed; why not, in words, or null when added.</summary>
    public string? AddUser(string name, string password)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace) || name.Contains(':'))
            return "A user name needs at least one character, and no spaces or colons.";
        if (_users.ContainsKey(name)) return $"There is already a user called {name}.";
        if (password.Length == 0) return "A user needs a password.";
        _users[name] = PasswordCheck.Hash(password);
        return null;
    }

    /// <summary>Replaces a user's password with a hash of a new one; why not, or null when done.</summary>
    public string? SetPassword(string name, string password)
    {
        if (!_users.ContainsKey(name)) return $"There is no user called {name}.";
        if (password.Length == 0) return "A password cannot be empty.";
        _users[name] = PasswordCheck.Hash(password);
        return null;
    }

    /// <summary>Removes a user.</summary>
    public void RemoveUser(string name) => _users.Remove(name);

    private int? Port => int.TryParse(PortText.Trim(), out var p) && p is >= 1 and <= 65535 ? p : null;

    private int? Idle => int.TryParse(IdleText.Trim(), out var n) && n >= 0 ? n : null;

    private IReadOnlyList<string> Roots =>
        [.. RootsText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private bool UsersChanged =>
        _users.Count != _originalUsers.Count || _users.Any(u => !_originalUsers.TryGetValue(u.Key, out var v) || v != u.Value);

    /// <summary>Whether anything differs from what the file says.</summary>
    public bool IsDirty =>
        Port != _original.Port || Bind.Trim() != _original.Bind || SelfSigned != _original.SelfSigned
        || Login != _original.Login || Idle != _original.IdleMinutes
        || string.Join("\n", Roots) != _originalRoots || UsersChanged;

    /// <summary>
    /// Why this cannot be saved as it stands, in words for the user; null when it can. Each is something the server
    /// would refuse at start, said here instead.
    /// </summary>
    public string? Problem
    {
        get
        {
            if (Port is null) return "The port must be a number from 1 to 65535.";
            if (!IPAddress.TryParse(Bind.Trim(), out var address))
                return "Listen on must be an IP address, such as 127.0.0.1 for this machine or 0.0.0.0 for every interface.";
            if (Roots.FirstOrDefault(r => !SandboxSettings.IsAnchored(r)) is { } relative)
                return $"Folders must start with / or ~ — '{relative}' would mean a different place on each launch.";
            if (Idle is null) return "Idle minutes must be a whole number, 0 or more.";
            var required = Login == LoginMode.Always || (Login == LoginMode.Auto && !IPAddress.IsLoopback(address));
            if (required && _users.Count == 0)
                return "A login is required here, but there is no user to log in as. Add one under Users.";
            return null;
        }
    }

    /// <summary>What was just written becomes what the file says, so the draft is clean without being rebuilt.</summary>
    public void Saved()
    {
        _original = new WebSettings(Port ?? WebSettings.Default.Port, Bind.Trim(), SelfSigned, Login,
            new Dictionary<string, string>(), Roots, Idle ?? WebSettings.Default.IdleMinutes);
        _originalRoots = string.Join("\n", Roots);
        _originalUsers = new Dictionary<string, string>(_users, StringComparer.Ordinal);
    }

    /// <summary>Writes what changed into the config's root.</summary>
    public void Apply(JsonObject root)
    {
        var d = WebSettings.Default;
        if (Port != _original.Port) Set(root, "port", Port == d.Port ? null : JsonValue.Create(Port!.Value));
        if (Bind.Trim() != _original.Bind) Set(root, "bind", Bind.Trim() == d.Bind ? null : JsonValue.Create(Bind.Trim()));
        if (SelfSigned != _original.SelfSigned) Set(root, "selfSigned", SelfSigned ? JsonValue.Create(true) : null);
        if (Login != _original.Login) Set(root, "login", Login == d.Login ? null : JsonValue.Create(Login.ToString().ToLowerInvariant()));
        if (Idle != _original.IdleMinutes) Set(root, "idleMinutes", Idle == d.IdleMinutes ? null : JsonValue.Create(Idle!.Value));
        if (string.Join("\n", Roots) != _originalRoots)
            Set(root, "roots", Roots.Count == 0 ? null : new JsonArray([.. Roots.Select(r => (JsonNode)JsonValue.Create(r))]));
        if (UsersChanged)
        {
            JsonObject? users = null;
            if (_users.Count > 0)
            {
                users = [];
                foreach (var (name, stored) in _users.OrderBy(u => u.Key, StringComparer.Ordinal)) users[name] = stored;
            }
            Set(root, "users", users);
        }
    }

    // ONE KEY IN THE web BLOCK, or removed when null — with the block when that empties it.
    private static void Set(JsonObject root, string key, JsonNode? value)
    {
        var web = root["web"] as JsonObject;
        if (value is null)
        {
            if (web is null) return;
            web.Remove(key);
            if (web.Count == 0) root.Remove("web");
            return;
        }
        if (web is null) root["web"] = web = [];
        web[key] = value;
    }
}
