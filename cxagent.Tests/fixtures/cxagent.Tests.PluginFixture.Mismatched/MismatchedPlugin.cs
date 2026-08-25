using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests.PluginFixture.Mismatched;

/// <summary>Load returns a manifest whose tool list differs from its own sidecar
/// (MismatchedPlugin.plugin.json) — proves ManagedPluginLoader refuses a plugin whose two
/// descriptions of itself disagree, rather than trusting whichever one it read last.</summary>
public sealed class MismatchedPlugin : IPlugin
{
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
        Task.FromResult(new PluginManifest("mismatched", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("a_different_tool", "not what the sidecar said",
                JsonSerializer.SerializeToElement(new { type = "object" }))])
        {
            // MATCHES ITS SIDECAR: this fixture exists to differ on TOOL NAMES, and a contract
            // mismatch here would make it fail for the wrong reason.
            Contract = 2,
        });

    public Task Start(CancellationToken ct) => Task.CompletedTask;

    public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct) =>
        Task.FromResult(new JobResult { Success = true });

    public Task Stop(CancellationToken ct) => Task.CompletedTask;
}
