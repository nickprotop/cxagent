using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>shellDetachOnTimeout</c>, which exists because the old behaviour is somebody's requirement: a
/// deadline that KILLS is what a machine running unattended jobs may need — a command whose side
/// effects must not outlive the call that asked for it, or a host that will not be alive to hear the
/// exit report. The default is the other way round, because for an interactive session a two-minute
/// deadline means the caller stopped waiting and not that a build should be destroyed.
/// </summary>
public class ShellDetachOnTimeoutConfigTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("cxagent-shell-detach-cfg").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ProviderSettings Load(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), json);
        return ProviderConfigLoader.LoadAndValidate(new AppPaths(_dir), new Dictionary<string, string>());
    }

    private const string Providers = """
        "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://localhost:1/v1" } },
        "defaultProvider": "local"
        """;

    // THE KEY IS READ. Config is parsed by hand with TryGetProperty, so a property added to the
    // settings record binds nothing until the loader asks for it — a key that looks configured and
    // changes nothing is exactly the failure this catches.
    [Fact]
    public void TheKeyIsRead()
    {
        Assert.False(Load($$"""{ {{Providers}}, "shellDetachOnTimeout": false }""").ShellDetachOnTimeout);
        Assert.True(Load($$"""{ {{Providers}}, "shellDetachOnTimeout": true }""").ShellDetachOnTimeout);
    }

    // ABSENT IS NULL HERE AND TRUE DOWNSTREAM, which is the split that matters: the loader must not
    // invent a value, because "the user said true" and "the user said nothing" are different facts —
    // and only the reader gets to decide what nothing means.
    [Fact]
    public void AbsentIsNull()
    {
        Assert.Null(Load($$"""{ {{Providers}} }""").ShellDetachOnTimeout);
    }

    // A MALFORMED VALUE WARNS AND KEEPS THE DEFAULT. Refusing to start would take providers and
    // session down over a behaviour switch; ignoring it silently would leave a user believing their
    // machine kills on a deadline when it hands the command over.
    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void AMalformedValueWarnsAndKeepsTheDefault(string written)
    {
        var settings = Load($$"""{ {{Providers}}, "shellDetachOnTimeout": {{written}} }""");

        Assert.Null(settings.ShellDetachOnTimeout);
        Assert.Contains(settings.Warnings, w => w.Contains("shellDetachOnTimeout"));
    }

    /// <summary>
    /// THE VALUE SURVIVES THE TRIP TO <see cref="ResolvedConfig"/>, which is a separate question from
    /// whether the loader read it.
    ///
    /// <para>THERE ARE TWO PLACES A CATALOG IS BUILT — the ordinary startup path and the one a
    /// <c>/model</c> switch takes — and a key carried by only one of them takes effect only after
    /// switching models. That has happened in this file before, to <c>Tools</c> and to
    /// <c>PluginPaths</c>: a configured value that worked after a switch and not on launch. So this
    /// asserts the value a SESSION would see, not the one the parser produced.</para>
    /// </summary>
    [Fact]
    public void TheValueReachesAResolvedConfig()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"),
            $$"""{ {{Providers}}, "shellDetachOnTimeout": false }""");

        var env = new Dictionary<string, string>();

        // BOTH SITES, IN ONE TEST, because the defect is always "one of the two". Resolve is launch;
        // ResolveInstance is what /model takes — and asserting only the first is how Tools and
        // PluginPaths each shipped working after a switch and not on startup.
        Assert.False(ConfigResolver.Resolve(new AppPaths(_dir), env, useMock: false)
            .ShellDetachOnTimeout);
        Assert.False(ConfigResolver.ResolveInstance(new AppPaths(_dir), env, "local")!
            .ShellDetachOnTimeout);
    }

    /// <summary>An unconfigured machine detaches, and a caller need not know that: the property is
    /// non-nullable precisely so nobody re-decides the default at each call site.</summary>
    [Fact]
    public void AnUnconfiguredMachineDetaches()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{ {{Providers}} }""");

        Assert.True(ConfigResolver.Resolve(new AppPaths(_dir), new Dictionary<string, string>(), useMock: false)
            .ShellDetachOnTimeout);
    }
}
