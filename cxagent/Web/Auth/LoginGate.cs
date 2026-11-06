using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using CxAgent.Core.Web;

namespace CxAgent.Web.Auth;

/// <summary>Why the server will not start with the login settings it has.</summary>
public enum StartRefusal
{
    /// <summary>It may start.</summary>
    None,

    /// <summary>A login is required and there is nobody who could log in.</summary>
    NoUsersForLogin,
}

/// <summary>What a login attempt came to: a token, or how long this address must wait before trying again.</summary>
public sealed record LoginResult(string? Token, TimeSpan RetryAfter);

/// <summary>
/// Who may use the web server: whether a login is required where it listens, who may log in, and the tokens their
/// browsers hold afterwards.
///
/// <para>A LOGIN IS A SHELL ON THIS MACHINE — whoever gets in drives an agent that runs commands here — so every
/// default leans shut: <see cref="LoginMode.Auto"/> requires a login anywhere but loopback, and a mode that would
/// require one with nobody able to log in refuses to start rather than start open.</para>
///
/// <para>TOKENS LIVE IN MEMORY. A restart logs everyone out, and nothing on disk can be stolen to log in later.</para>
/// </summary>
public sealed class LoginGate(WebSettings settings, bool loopbackBind, TimeProvider clock)
{
    /// <summary>The cookie's name.</summary>
    public const string CookieName = "cxagent_web";

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    private const int MaxTracked = 10_000;
    private static readonly TimeSpan Forget = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (string User, DateTimeOffset Expires)> _tokens = new();
    private readonly Dictionary<IPAddress, (int Failures, DateTimeOffset Until)> _failures = [];
    private readonly HashSet<IPAddress> _checking = [];
    private readonly object _gate = new();

    // TWO PASSWORD CHECKS AT A TIME, WHOEVER ASKS. A hashed password costs a third of a second of CPU; with no limit,
    // logins from many addresses at once would take every core.
    private readonly SemaphoreSlim _hashing = new(2, 2);

    // AN UNKNOWN NAME COSTS WHAT A KNOWN ONE DOES. When any user has a hash, a name that does not exist is checked
    // against a hash too, so the time a refusal takes does not say which names are real.
    private readonly Lazy<string> _decoy = new(() =>
        settings.Users.Values.Any(PasswordCheck.IsHash) ? PasswordCheck.Hash(Guid.NewGuid().ToString()) : "\0no such user\0");

    /// <summary>How many addresses the backoff is tracking — for tests of its forgetting.</summary>
    public int TrackedAddresses
    {
        get { lock (_gate) return _failures.Count; }
    }

    /// <summary>Whether a browser must log in before it gets a session.</summary>
    public bool LoginRequired => settings.Login switch
    {
        LoginMode.Always => true,
        LoginMode.Off => false,
        _ => !loopbackBind,
    };

    /// <summary>Whether the page says, every time, that anyone who reaches the port gets in.</summary>
    public bool ShowsNoLoginBanner => settings.Login == LoginMode.Off && !loopbackBind;

    /// <summary>Whether the server may start: a login that is required needs someone who can give it.</summary>
    public StartRefusal CanStart() =>
        LoginRequired && settings.Users.Count == 0 ? StartRefusal.NoUsersForLogin : StartRefusal.None;

    /// <summary>
    /// Checks a user and password from <paramref name="from"/>.
    ///
    /// <para>EACH FAILURE MAKES THAT ADDRESS WAIT LONGER — 1, 2, 4 … up to 30 seconds — and while it waits, even the
    /// right password is not checked, so the wait cannot be used to test guesses. Another address is not held up.
    /// A success clears it.</para>
    /// </summary>
    public LoginResult Login(string user, string password, IPAddress from)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            Sweep(now);

            // ONE CHECK IN FLIGHT PER ADDRESS. Parallel guesses would otherwise all pass the wait before any of them
            // recorded its failure.
            if (_failures.TryGetValue(from, out var held) && held.Until > now) return new LoginResult(null, held.Until - now);
            if (!_checking.Add(from)) return new LoginResult(null, TimeSpan.FromSeconds(1));
        }

        bool ok;
        try
        {
            if (!_hashing.Wait(0)) return new LoginResult(null, TimeSpan.FromSeconds(1));
            try
            {
                var known = settings.Users.TryGetValue(user, out var stored);
                ok = PasswordCheck.Matches(known ? stored! : _decoy.Value, password) && known;
            }
            finally
            {
                _hashing.Release();
            }
        }
        finally
        {
            lock (_gate) _checking.Remove(from);
        }

        lock (_gate)
        {
            if (!ok)
            {
                var failures = (_failures.TryGetValue(from, out var before) ? before.Failures : 0) + 1;
                var wait = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failures - 1), MaxWait.TotalSeconds));
                _failures[from] = (failures, now + wait);
                return new LoginResult(null, TimeSpan.Zero);
            }
            _failures.Remove(from);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tokens[token] = (user, now + Lifetime);
        return new LoginResult(token, TimeSpan.Zero);
    }

    // FORGET WHAT NO LONGER MATTERS: failures an hour past their wait, and expired tokens. Bounded either way, so an
    // attacker rotating addresses cannot grow the table without limit.
    private void Sweep(DateTimeOffset now)
    {
        foreach (var (address, f) in _failures.ToList())
            if (f.Until + Forget < now) _failures.Remove(address);
        if (_failures.Count > MaxTracked)
            foreach (var address in _failures.OrderBy(f => f.Value.Until).Take(_failures.Count - MaxTracked).Select(f => f.Key).ToList())
                _failures.Remove(address);
        foreach (var (token, t) in _tokens)
            if (t.Expires <= now) _tokens.TryRemove(token, out _);
    }

    /// <summary>The user a token belongs to; null when it is unknown or expired.</summary>
    public string? UserFor(string? token)
    {
        if (token is null || !_tokens.TryGetValue(token, out var t)) return null;

        // A USER TAKEN OUT OF THE CONFIG IS LOGGED OUT, whatever their browser still holds.
        if (t.Expires > clock.GetUtcNow() && settings.Users.ContainsKey(t.User)) return t.User;
        _tokens.TryRemove(token, out _);
        return null;
    }

    /// <summary>Forgets a token: the browser holding it is logged out.</summary>
    public void Logout(string token) => _tokens.TryRemove(token, out _);

    /// <summary>
    /// The <c>Set-Cookie</c> value for a token.
    ///
    /// <para>HttpOnly so the page's own script cannot read it — model output is on that page. SameSite=Strict so
    /// another site's form cannot post with it. Secure when served over TLS.</para>
    /// </summary>
    public static string Cookie(string token, bool secure) =>
        $"{CookieName}={token}; HttpOnly; SameSite=Strict; Path=/; Max-Age={(int)Lifetime.TotalSeconds}" + (secure ? "; Secure" : "");

    /// <summary>The <c>Set-Cookie</c> value that clears the cookie.</summary>
    public static string ClearedCookie(bool secure) =>
        $"{CookieName}=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0" + (secure ? "; Secure" : "");

    /// <summary>This server's token from a request's <c>Cookie</c> header, or null.</summary>
    public static string? TokenFromCookieHeader(string? header)
    {
        if (header is null) return null;
        foreach (var part in header.Split(';'))
        {
            var kv = part.Trim();
            if (kv.StartsWith(CookieName + "=", StringComparison.Ordinal)) return kv[(CookieName.Length + 1)..];
        }
        return null;
    }
}
