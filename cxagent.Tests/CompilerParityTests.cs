using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Llm;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Every plan-compile guard, asserted against BOTH compilers from ONE table (D12).
///
/// <para>WHY THIS FILE EXISTS. <c>PlanCompiler.BuildDag</c> (the initial plan) and
/// <c>ConsultJobCompiler</c> (jobs added mid-goal) are siblings that MIRROR each other by hand —
/// ConsultJobCompiler's own header says so. Nothing enforced that a rule added to one reached the
/// other, and the cost was measured, repeatedly:</para>
///
/// <list type="bullet">
///   <item><b>D8</b> — reference validation existed ONLY in the consult path. The initial plan, where
///   the live fan-out failures actually happened, had none at all.</item>
///   <item><b>D7</b> — the review-write boundary had to be wired into both, separately.</item>
///   <item><b>D11</b> — the depends_on check had been in ConsultJobCompiler since D6. PlanCompiler
///   never got it. Measured: 1 of 5 fan-out trials still wrote a literal placeholder to disk AFTER
///   D8 landed.</item>
/// </list>
///
/// <para>Each was found by a LIVE DRIVE, not by tests — the tests passed every time, because a fix
/// lands in whichever compiler the failing test exercised and looks complete.</para>
///
/// <para>So: one table, two compilers, same expectations. Adding a guard to one compiler without the
/// other now fails HERE, at the point the drift is introduced, rather than on a drive weeks later.
/// A new guard should gain a row rather than a new pair of near-identical tests.</para>
///
/// <para>NOT asserted here: identical error TEXT or failure MECHANISM. The two differ by design —
/// PlanCompiler throws, ConsultJobCompiler returns false with an out-param, and the consult path
/// additionally resolves against the LIVE dag. Only "is this condition rejected at compile time?"
/// is common to both, and that is what this pins.</para>
/// </summary>
public class CompilerParityTests
{
    /// <summary>A condition both compilers must reject, expressed as a one-job-or-two plan body.</summary>
    public sealed record GuardCase(string Name, string JobsJson, string ExpectedInMessage);

    public static TheoryData<GuardCase> GuardedConditions() => new()
    {



        new GuardCase(
            "a MISSPELLED param — named, not reported as merely absent",
            """
            [ { "id":"r", "name":"R", "type":"file",
                "params":{ "action":"read", "file_path":"/tmp/parity.md" } } ]
            """,
            "did you mean 'path'"),

        new GuardCase(
            "AMBIGUOUS WRITE: several dependencies, no authored content",
            """
            [ { "id":"a", "name":"A", "type":"shell", "params":{ "command":"echo a" } },
              { "id":"b", "name":"B", "type":"shell", "params":{ "command":"echo b" } },
              { "id":"w", "name":"W", "type":"file", "depends_on":["a","b"],
                "params":{ "action":"write", "path":"/tmp/parity.md" } } ]
            """,
            "ambiguous"),

        new GuardCase(
            "a leftover {{ref}} as a write's whole content — literal text now, not a reference",
            """
            [ { "id":"r", "name":"R", "type":"llm_agent",
                "params":{ "role":"reviewer", "prompt":"review it" } },
              { "id":"w", "name":"W", "type":"file", "depends_on":["r"],
                "params":{ "action":"write", "path":"/tmp/parity.md", "content":"{{r.content}}" } } ]
            """,
            "literal text"),

        new GuardCase(
            "a file replace fed by a read of the SAME file — the pattern cannot survive a digest",
            """
            [ { "id":"r", "name":"R", "type":"file",
                "params":{ "action":"read", "path":"/tmp/parity-src.cs" } },
              { "id":"e", "name":"E", "type":"file", "depends_on":["r"],
                "params":{ "action":"replace", "path":"/tmp/parity-src.cs",
                           "pattern":"return x * 3;", "replacement":"return y;" } } ]
            """,
            "cannot reproduce exact bytes"),
    };

    /// <summary>
    /// A registry that HAS <c>llm_agent</c>. The no-arg <c>CreateWithBuiltins()</c> deliberately omits
    /// it — P8b registers it only when a RoleResolver is supplied, because a job type that always
    /// fails is worse than one that is never advertised. The D7 case is about a ROLE, so it needs the
    /// roled plugin present or the compiler rejects "unknown plugin type 'llm_agent'" long before the
    /// boundary rule is reached.
    /// </summary>
    private static PluginRegistry RegistryWithRoles()
    {
        var registry = ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["local"] = new MockLlmProvider() }, "local");
        return PluginRegistry.CreateWithBuiltins(registry, PermissionGate.AllowAll, fanOut: true);
    }

    [Theory]
    [MemberData(nameof(GuardedConditions))]
    public void PlanCompiler_RejectsIt(GuardCase c)
    {
        var plan = JsonDocument.Parse($$"""{ "summary":"x", "jobs": {{c.JobsJson}} }""").RootElement;

        // WITH the registry. Called without it, PlanCompiler skips the plugin's own param check
        // entirely (`plugins is not null` guards it), so every rule that lives in a plugin's
        // Validate passed this harness vacuously — while ConsultJobCompiler, which takes a registry
        // as a required argument, checked them. The two compilers were not being compared on the
        // same rules, which is the one thing this file exists to guarantee.
        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanCompiler.BuildDag("g", plan, RegistryWithRoles()));
        Assert.Contains(c.ExpectedInMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(GuardedConditions))]
    public void ConsultJobCompiler_RejectsIt(GuardCase c)
    {
        var mod = new ConsultModification(
            JsonDocument.Parse(c.JobsJson).RootElement.Clone(),
            new Dictionary<string, JsonElement>());

        var ok = ConsultJobCompiler.TryCompile(
            new JobDag(), "g", mod, RegistryWithRoles(), out _, out var error);

        Assert.False(ok, $"ConsultJobCompiler accepted a plan PlanCompiler rejects: {c.Name}");
        Assert.NotNull(error);
        Assert.Contains(c.ExpectedInMessage, error!, StringComparison.OrdinalIgnoreCase);
    }
}
