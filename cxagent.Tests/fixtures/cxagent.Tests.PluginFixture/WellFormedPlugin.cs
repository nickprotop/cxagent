using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests.PluginFixture;

/// <summary>The clean case: Load returns exactly what its own sidecar (WellFormedPlugin.plugin.json,
/// copied to test output as content) declares. Used to prove a matching plugin loads.</summary>
public sealed class WellFormedPlugin : IPlugin
{
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
        Task.FromResult(new PluginManifest("well-formed", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("wf_tool", "a fixture tool",
                JsonSerializer.SerializeToElement(new { type = "object" }))])
        {
            Contract = 2,
        });

    public Task Start(CancellationToken ct) => Task.CompletedTask;

    public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct) =>
        Task.FromResult(new JobResult { Success = true });

    public Task Stop(CancellationToken ct) => Task.CompletedTask;
}
