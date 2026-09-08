using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests.PluginFixture.ClaimsClient;

/// <summary>Load returns a manifest with Client = true, agreeing with its own sidecar
/// (ClaimsClientPlugin.plugin.json) — so PluginManifestMatch has nothing to refuse — but the class
/// implements only IPlugin, not IPluginClientConsumer. Proves ManagedPluginLoader's own capability
/// check refuses a manifest the binary cannot honour, a different failure than a sidecar/Load
/// disagreement: MismatchedPlugin covers that one, this fixture exists because that check cannot be
/// reached through it.</summary>
public sealed class ClaimsClientPlugin : IPlugin
{
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
        Task.FromResult(new PluginManifest("claims-client", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("cc_tool", "a fixture tool",
                JsonSerializer.SerializeToElement(new { type = "object" }))])
        {
            Contract = 2,
            Client = true,
        });

    public Task Start(CancellationToken ct) => Task.CompletedTask;

    public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct) =>
        Task.FromResult(new JobResult { Success = true });

    public Task Stop(CancellationToken ct) => Task.CompletedTask;
}
