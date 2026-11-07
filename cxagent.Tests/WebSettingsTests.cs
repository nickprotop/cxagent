using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary>config.json's <c>web</c> block: what it reads, what it refuses, and what a wrong shape falls back to.</summary>
public sealed class WebSettingsTests
{
    private static (WebSettings Settings, List<string> Warnings) Read(string json)
    {
        var warnings = new List<string>();
        using var doc = JsonDocument.Parse(json);
        return (WebSettings.Read(doc.RootElement, warnings), warnings);
    }

    [Fact]
    public void Absent_block_is_default()
    {
        var (s, w) = Read("{}");
        Assert.Equal(WebSettings.Default, s);
        Assert.Equal(5180, s.Port);
        Assert.Equal("127.0.0.1", s.Bind);
        Assert.Equal(LoginMode.Auto, s.Login);
        Assert.Equal(30, s.IdleMinutes);
        Assert.Empty(w);
    }

    [Fact]
    public void All_fields_round_trip()
    {
        var (s, w) = Read("""
            { "web": { "port": 6001, "bind": "0.0.0.0", "selfSigned": true, "login": "always",
                       "users": { "nick": "pw", "ana": "pbkdf2:x" }, "roots": ["~/source", "/srv/x"], "idleMinutes": 0 } }
            """);
        Assert.Empty(w);
        Assert.Equal(6001, s.Port);
        Assert.Equal("0.0.0.0", s.Bind);
        Assert.True(s.SelfSigned);
        Assert.Equal(LoginMode.Always, s.Login);
        Assert.Equal("pw", s.Users["nick"]);
        Assert.Equal("pbkdf2:x", s.Users["ana"]);
        Assert.Equal(["~/source", "/srv/x"], s.Roots);
        Assert.Equal(0, s.IdleMinutes);
    }

    [Fact]
    public void Relative_root_is_refused_with_warning()
    {
        var (s, w) = Read("""{ "web": { "roots": ["projects", "~/ok"] } }""");
        Assert.Equal(["~/ok"], s.Roots);
        Assert.Contains(w, x => x.Contains("web.roots", StringComparison.Ordinal) && x.Contains("'projects' is relative", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("\"80\"")]
    public void Bad_port_warns_and_defaults(string port)
    {
        var (s, w) = Read($$"""{ "web": { "port": {{port}} } }""");
        Assert.Equal(5180, s.Port);
        Assert.Contains(w, x => x.Contains("web.port", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_login_mode_warns_and_is_auto()
    {
        var (s, w) = Read("""{ "web": { "login": "sometimes" } }""");
        Assert.Equal(LoginMode.Auto, s.Login);
        Assert.Contains(w, x => x.Contains("web.login", StringComparison.Ordinal));
    }

    [Fact]
    public void Login_mode_ignores_case() =>
        Assert.Equal(LoginMode.Off, Read("""{ "web": { "login": "OFF" } }""").Settings.Login);

    [Fact]
    public void Users_must_be_strings()
    {
        var (s, w) = Read("""{ "web": { "users": { "nick": 5, "ana": "pw" } } }""");
        Assert.False(s.Users.ContainsKey("nick"));
        Assert.Equal("pw", s.Users["ana"]);
        Assert.Contains(w, x => x.Contains("web.users", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrong_shaped_block_warns_and_is_default()
    {
        var (s, w) = Read("""{ "web": 3 }""");
        Assert.Equal(WebSettings.Default, s);
        Assert.Single(w);
    }

    [Fact]
    public void ResolvedConfig_exposes_web()
    {
        var settings = ProviderConfigLoader.Parse("""
            { "providers": { "local": { "kind": "ollama", "model": "m" } }, "defaultProvider": "local",
              "web": { "port": 6002 } }
            """, new Dictionary<string, string>());
        Assert.Equal(6002, settings.Web?.Port);
        var resolved = ResolvedConfig.ForTesting(new MockLlmProvider());
        Assert.Equal(WebSettings.Default, resolved.Web);
    }

    // ---- web.allow: what a browser may do beyond the conversation ---------------------------------------------------

    [Fact]
    public void Without_an_allow_block_plugins_are_on_and_the_rest_off()
    {
        var a = Read("{}").Settings.Allow;
        Assert.Equal(new WebAccess(Shell: false, Config: false, Plugins: true, OpenAnywhere: false), a);
    }

    [Fact]
    public void Each_switch_is_read_and_one_left_out_keeps_its_default()
    {
        var (s, w) = Read("""{ "web": { "allow": { "shell": true, "openAnywhere": true } } }""");
        Assert.Empty(w);
        Assert.Equal(new WebAccess(Shell: true, Config: false, Plugins: true, OpenAnywhere: true), s.Allow);
    }

    [Fact]
    public void Plugins_can_be_turned_off()
    {
        Assert.False(Read("""{ "web": { "allow": { "plugins": false } } }""").Settings.Allow.Plugins);
    }

    [Fact]
    public void A_switch_of_the_wrong_shape_warns_and_stays_at_its_default()
    {
        // OFF, NOT ON, for the three that widen what a login reaches: a typo must not be what opens a shell.
        var (s, w) = Read("""{ "web": { "allow": { "shell": "yes", "config": 1 } } }""");
        Assert.False(s.Allow.Shell);
        Assert.False(s.Allow.Config);
        Assert.Equal(2, w.Count);
    }

    [Fact]
    public void An_allow_that_is_not_an_object_warns_and_keeps_the_defaults()
    {
        var (s, w) = Read("""{ "web": { "allow": true } }""");
        Assert.Equal(WebAccess.Default, s.Allow);
        Assert.Single(w);
    }
}
