using CxAgent.Core.Llm;
using CxAgent.Core.Sandbox;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The sandbox's settings: on in the app when config.json says nothing, off for an embedder whose
/// <see cref="AgentConfig"/> says nothing — the two hosts start from opposite defaults on purpose.
/// </summary>
public class SandboxSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sandbox-settings-" + Guid.NewGuid().ToString("N"));

    public SandboxSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private static readonly Dictionary<string, string> NoEnv = new();

    private static ProviderSettings Parse(string extra) => ProviderConfigLoader.Parse($$"""
        { "providers": { "local": { "kind": "ollama", "model": "m" } }, "defaultProvider": "local" {{extra}} }
        """, NoEnv);

    private static AgentConfig Agent(SandboxSettings? sandbox) => new()
    {
        Models = { ["local"] = new(ProviderKind.OpenAiCompatible, "m") { BaseUrl = "http://localhost:1/v1" } },
        DefaultModel = "local",
        Sandbox = sandbox,
    };

    [Fact]
    public void Absent_block_in_config_json_is_on()
    {
        var sandbox = Parse("").Sandbox;
        Assert.NotNull(sandbox);
        Assert.True(sandbox.Enabled);
        Assert.True(sandbox.Network);
        Assert.Empty(sandbox.Writable);
    }

    [Fact]
    public void Block_reads_every_field()
    {
        var sandbox = Parse("""
            , "sandbox": { "enabled": false, "network": false, "writable": ["~/out"], "hidden": ["~/.secret"],
                           "hiddenEnv": ["MY_*"], "passEnv": ["NPM_TOKEN"] }
            """).Sandbox!;
        Assert.False(sandbox.Enabled);
        Assert.False(sandbox.Network);
        Assert.Equal(["~/out"], sandbox.Writable);
        Assert.Equal(["~/.secret"], sandbox.Hidden);
        Assert.Equal(["MY_*"], sandbox.HiddenEnv);
        Assert.Equal(["NPM_TOKEN"], sandbox.PassEnv);
    }

    [Fact]
    public void Wrong_shape_warns_and_keeps_the_default()
    {
        var settings = Parse(""", "sandbox": "off" """);
        Assert.True(settings.Sandbox!.Enabled);
        Assert.Contains(settings.Warnings, w => w.Contains("sandbox"));
    }

    /// <summary>BOTH CATALOG SITES — launch and a /model switch — since a key carried by one of them works only
    /// after switching models.</summary>
    [Fact]
    public void Config_json_sandbox_reaches_the_resolved_config()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """
            { "providers": { "local": { "kind": "ollama", "model": "m" } }, "defaultProvider": "local",
              "sandbox": { "network": false } }
            """);
        var launch = ConfigResolver.Resolve(new AppPaths(_dir), NoEnv, useMock: false).Sandbox;
        var switched = ConfigResolver.ResolveInstance(new AppPaths(_dir), NoEnv, "local")!.Sandbox;
        Assert.True(launch.Enabled);
        Assert.False(launch.Network);
        Assert.False(switched.Network);
    }

    [Fact]
    public void AgentConfig_without_sandbox_is_off()
    {
        var resolved = Agent(null).Resolve();
        Assert.Empty(resolved.Errors);
        Assert.False(resolved.Sandbox.Enabled);
    }

    [Fact]
    public void AgentConfig_sandbox_is_carried_to_resolved()
    {
        var resolved = Agent(SandboxSettings.AppDefault with { Network = false }).Resolve();
        Assert.True(resolved.Sandbox.Enabled);
        Assert.False(resolved.Sandbox.Network);
    }

    [Fact]
    public void Tilde_paths_are_kept_as_written() =>
        Assert.Equal(["~/a"], Parse(""", "sandbox": { "writable": ["~/a"] } """).Sandbox!.Writable);

    /// <summary>A RELATIVE PATH WOULD RESOLVE AGAINST WHEREVER cxagent WAS STARTED, a different folder each time: refused
    /// with a warning rather than guessed at.</summary>
    [Fact]
    public void Relative_paths_are_dropped_with_a_warning()
    {
        var settings = Parse(""", "sandbox": { "writable": ["build/out", "~/ok", "/abs"], "hidden": ["secret"] } """);
        Assert.Equal(["~/ok", "/abs"], settings.Sandbox!.Writable);
        Assert.Empty(settings.Sandbox.Hidden);
        Assert.Contains(settings.Warnings, w => w.Contains("build/out"));
    }
}
