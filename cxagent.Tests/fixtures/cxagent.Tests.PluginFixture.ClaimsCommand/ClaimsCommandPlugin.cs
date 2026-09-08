using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests.PluginFixture.ClaimsCommand;

/// <summary>Load returns a manifest declaring one command, agreeing with its own sidecar
/// (ClaimsCommandPlugin.plugin.json) — so PluginManifestMatch has nothing to refuse — but the class
/// implements only IPlugin, not IPluginCommandHandler. Proves ManagedPluginLoader's own capability
/// check refuses a manifest the binary cannot honour, a different failure than a sidecar/Load
/// disagreement over commands: that drift is caught by PluginManifestMatch before this branch is
/// ever reached, which is exactly why this fixture exists — see ClaimsClientPlugin, its precedent
/// for the client capability.</summary>
public sealed class ClaimsCommandPlugin : IPlugin
{
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
        Task.FromResult(new PluginManifest("claims-command", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("cmd_tool", "a fixture tool",
                JsonSerializer.SerializeToElement(new { type = "object" }))],
            [new PluginCommandManifest("nope", "cannot run")])
        {
            Contract = 2,
        });

    public Task Start(CancellationToken ct) => Task.CompletedTask;

    public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct) =>
        Task.FromResult(new JobResult { Success = true });

    public Task Stop(CancellationToken ct) => Task.CompletedTask;
}
