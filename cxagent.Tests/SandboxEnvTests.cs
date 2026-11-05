using CxAgent.Core.Sandbox;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Which variables a contained command loses: credentials by pattern, the session's sockets, and the
/// names config reads its keys from — with the user's own additions and exceptions.</summary>
public class SandboxEnvTests
{
    private static IReadOnlyList<string> Unset(Dictionary<string, string> env, SandboxSettings? settings = null,
        string[]? configKeys = null) =>
        SandboxEnv.ToUnset(env, settings ?? SandboxSettings.AppDefault, configKeys ?? []);

    private static Dictionary<string, string> Env(params string[] names) => names.ToDictionary(n => n, _ => "x");

    [Fact]
    public void Credential_patterns_are_unset()
    {
        var gone = Unset(Env("OPENAI_API_KEY", "CXAGENT_PROVIDER_X_APIKEY", "GITHUB_TOKEN", "CLIENT_SECRET_ID",
            "DB_PASSWORD", "AWS_REGION"));
        Assert.Equal(6, gone.Count);
    }

    [Fact]
    public void Plain_variables_pass() =>
        Assert.Empty(Unset(Env("PATH", "HOME", "LANG", "TERM", "DOTNET_ROOT")));

    [Fact]
    public void Dbus_and_ssh_agent_are_unset() =>
        Assert.Equal(2, Unset(Env("DBUS_SESSION_BUS_ADDRESS", "SSH_AUTH_SOCK")).Count);

    [Fact]
    public void Pass_env_keeps_a_matching_name() =>
        Assert.Equal(["GITHUB_TOKEN"], Unset(Env("NPM_TOKEN", "GITHUB_TOKEN"),
            SandboxSettings.AppDefault with { PassEnv = ["NPM_TOKEN"] }));

    [Fact]
    public void Hidden_env_adds_a_pattern() =>
        Assert.Equal(["MY_SERVICE_URL"], Unset(Env("MY_SERVICE_URL", "PATH"),
            SandboxSettings.AppDefault with { HiddenEnv = ["MY_SERVICE_*"] }));

    [Fact]
    public void Config_key_variables_are_unset() =>
        Assert.Equal(["LOCAL_LLM"], Unset(Env("LOCAL_LLM", "PATH"), configKeys: ["LOCAL_LLM"]));

    [Fact]
    public void Patterns_ignore_case() =>
        Assert.Single(Unset(Env("github_token")));

    [Fact]
    public void Display_and_terminal_multiplexer_variables_are_unset() =>
        Assert.Equal(4, Unset(Env("TMUX", "TMUX_PANE", "DISPLAY", "WAYLAND_DISPLAY")).Count);

    [Fact]
    public void More_credential_names_are_unset() =>
        Assert.Equal(5, Unset(Env("PGPASSWORD", "STRIPE_KEY", "GOOGLE_APPLICATION_CREDENTIALS", "DATABASE_URL", "MYSQL_PWD")).Count);
}
