using System.Net;
using CxAgent.Core.Web;
using CxAgent.Web.Auth;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Who gets into the web server: passwords plain or hashed, the three login modes, and the cookie that follows.</summary>
public sealed class LoginGateTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly IPAddress Phone = IPAddress.Parse("192.168.1.20");

    private static WebSettings With(LoginMode mode, params (string User, string Password)[] users) =>
        WebSettings.Default with { Login = mode, Users = users.ToDictionary(u => u.User, u => u.Password) };

    [Fact]
    public void Plain_password_matches()
    {
        Assert.True(PasswordCheck.Matches("secret", "secret"));
        Assert.False(PasswordCheck.Matches("secret", "Secret"));
        Assert.False(PasswordCheck.Matches("secret", ""));
    }

    [Fact]
    public void Hashed_password_matches_and_wrong_one_does_not()
    {
        var stored = PasswordCheck.Hash("correct horse");
        Assert.True(PasswordCheck.Matches(stored, "correct horse"));
        Assert.False(PasswordCheck.Matches(stored, "wrong horse"));
    }

    [Fact]
    public void Hash_has_the_documented_shape()
    {
        var parts = PasswordCheck.Hash("x").Split(':');
        Assert.Equal(["pbkdf2", "sha256", "600000"], parts[..3]);
        Assert.Equal(16, Convert.FromBase64String(parts[3]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[4]).Length);
    }

    [Theory]
    [InlineData("pbkdf2:sha256:nope")]
    [InlineData("pbkdf2:sha256:1:c2FsdA==:")]
    [InlineData("pbkdf2:sha256:600000:c2FsdA==:AAAA")]
    [InlineData("pbkdf2:sha256:10:AAAAAAAAAAAAAAAAAAAAAA==:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2:sha256:600000:!!:!!")]
    public void A_malformed_or_weak_hash_never_matches(string stored)
    {
        Assert.False(PasswordCheck.Matches(stored, ""));
        Assert.False(PasswordCheck.Matches(stored, "anything"));
    }

    // THE SPEC'S TABLE, ROW BY ROW: mode × where the server listens → must a browser log in, may it start at all.
    [Theory]
    [InlineData(LoginMode.Auto, true, true, false, StartRefusal.None)]
    [InlineData(LoginMode.Auto, false, true, true, StartRefusal.None)]
    [InlineData(LoginMode.Auto, false, false, true, StartRefusal.NoUsersForLogin)]
    [InlineData(LoginMode.Auto, true, false, false, StartRefusal.None)]
    [InlineData(LoginMode.Always, true, true, true, StartRefusal.None)]
    [InlineData(LoginMode.Always, true, false, true, StartRefusal.NoUsersForLogin)]
    [InlineData(LoginMode.Off, false, false, false, StartRefusal.None)]
    [InlineData(LoginMode.Off, true, false, false, StartRefusal.None)]
    public void Login_mode_table(LoginMode mode, bool loopback, bool hasUser, bool required, StartRefusal refusal)
    {
        var settings = hasUser ? With(mode, ("nick", "pw")) : With(mode);
        var gate = new LoginGate(settings, loopback, new Clock());
        Assert.Equal(required, gate.LoginRequired);
        Assert.Equal(refusal, gate.CanStart());
    }

    [Theory]
    [InlineData(LoginMode.Off, false, true)]
    [InlineData(LoginMode.Off, true, false)]
    [InlineData(LoginMode.Auto, false, false)]
    public void No_login_banner_only_when_off_beyond_this_machine(LoginMode mode, bool loopback, bool banner) =>
        Assert.Equal(banner, new LoginGate(With(mode), loopback, new Clock()).ShowsNoLoginBanner);

    [Fact]
    public void A_good_login_gives_a_token_that_names_the_user()
    {
        var gate = new LoginGate(With(LoginMode.Always, ("nick", "pw")), true, new Clock());
        var result = gate.Login("nick", "pw", Phone);
        Assert.NotNull(result.Token);
        Assert.Equal("nick", gate.UserFor(result.Token));
        Assert.Null(gate.UserFor("made-up"));
        Assert.Null(gate.UserFor(null));
    }

    [Fact]
    public void Unknown_user_and_wrong_password_both_fail()
    {
        var gate = new LoginGate(With(LoginMode.Always, ("nick", "pw")), true, new Clock());
        Assert.Null(gate.Login("ana", "pw", Phone).Token);
        Assert.Null(gate.Login("nick", "nope", IPAddress.Parse("10.0.0.9")).Token);
    }

    [Fact]
    public void Failed_logins_back_off_per_address()
    {
        var clock = new Clock();
        var gate = new LoginGate(With(LoginMode.Always, ("nick", "pw")), true, clock);

        Assert.Null(gate.Login("nick", "bad", Phone).Token);           // 1st failure: wait 1 s
        var waiting = gate.Login("nick", "pw", Phone);                 // even the right password waits
        Assert.Null(waiting.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), waiting.RetryAfter);

        Assert.NotNull(gate.Login("nick", "pw", IPAddress.Parse("10.0.0.9")).Token);   // another address is not held

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(gate.Login("nick", "bad", Phone).Token);           // 2nd: wait 2 s
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(TimeSpan.FromSeconds(1), gate.Login("nick", "pw", Phone).RetryAfter);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.NotNull(gate.Login("nick", "pw", Phone).Token);         // success resets
        Assert.Null(gate.Login("nick", "bad", Phone).Token);
        Assert.Equal(TimeSpan.FromSeconds(1), gate.Login("nick", "pw", Phone).RetryAfter);
    }

    [Fact]
    public void Backoff_is_capped_at_30_seconds()
    {
        var clock = new Clock();
        var gate = new LoginGate(With(LoginMode.Always, ("nick", "pw")), true, clock);
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(gate.Login("nick", "bad", Phone).Token);
            clock.Now += TimeSpan.FromMinutes(1);
        }
        Assert.Null(gate.Login("nick", "bad", Phone).Token);
        Assert.Equal(TimeSpan.FromSeconds(30), gate.Login("nick", "pw", Phone).RetryAfter);
    }

    [Fact]
    public void Parallel_guesses_from_one_address_do_not_slip_past_the_backoff()
    {
        var gate = new LoginGate(With(LoginMode.Always, ("nick", PasswordCheck.Hash("pw"))), true, new Clock());
        var results = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8)
            .Select(_ => gate.Login("nick", "bad", Phone)).ToList();
        // AT MOST ONE GUESS IS ACTUALLY CHECKED; the rest are told to wait.
        Assert.True(results.Count(r => r.RetryAfter > TimeSpan.Zero) >= 7);
    }

    [Fact]
    public void A_removed_user_s_token_stops_working()
    {
        var clock = new Clock();
        var gate = new LoginGate(With(LoginMode.Always, ("nick", "pw")), true, clock);
        var token = gate.Login("nick", "pw", Phone).Token;
        var later = new LoginGate(With(LoginMode.Always, ("ana", "pw")), true, clock);
        Assert.Null(later.UserFor(token));
        Assert.Equal("nick", gate.UserFor(token));
    }

    [Fact]
    public void Old_failures_are_forgotten()
    {
        var clock = new Clock();
        var gate = new LoginGate(With(LoginMode.Always, ("nick", "pw")), true, clock);
        for (var i = 0; i < 50; i++) gate.Login("nick", "bad", IPAddress.Parse($"10.0.0.{i}"));
        clock.Now += TimeSpan.FromHours(2);
        gate.Login("nick", "pw", Phone);
        Assert.Equal(0, gate.TrackedAddresses);
    }

    [Fact]
    public void Token_expires_after_30_days()
    {
        var clock = new Clock();
        var gate = new LoginGate(With(LoginMode.Always, ("nick", "pw")), true, clock);
        var token = gate.Login("nick", "pw", Phone).Token;
        clock.Now += TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1);
        Assert.Equal("nick", gate.UserFor(token));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Null(gate.UserFor(token));
    }

    [Fact]
    public void Logout_invalidates_token()
    {
        var gate = new LoginGate(With(LoginMode.Always, ("nick", "pw")), true, new Clock());
        var token = gate.Login("nick", "pw", Phone).Token!;
        gate.Logout(token);
        Assert.Null(gate.UserFor(token));
    }

    [Fact]
    public void Cookie_carries_the_flags()
    {
        var plain = LoginGate.Cookie("abc", secure: false);
        Assert.Equal("cxagent_web=abc; HttpOnly; SameSite=Strict; Path=/; Max-Age=2592000", plain);
        Assert.EndsWith("; Secure", LoginGate.Cookie("abc", secure: true), StringComparison.Ordinal);
        Assert.Equal("abc", LoginGate.TokenFromCookieHeader("theme=dark; cxagent_web=abc; x=1"));
        Assert.Null(LoginGate.TokenFromCookieHeader("theme=dark"));
    }
}
