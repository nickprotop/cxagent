using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests.PluginFixture.DeclaresClient;

/// <summary>
/// Declares <c>"client": true</c>, agrees with it in Load(), AND implements
/// <see cref="IPluginClientConsumer"/> — the one combination neither WellFormedPlugin nor
/// ClaimsClientPlugin can produce: WellFormedPlugin's Load() is hardcoded to Client=false (that
/// fixture's own doc: it returns exactly what its sidecar declares, and no sidecar can make it
/// return true), and ClaimsClientPlugin exists specifically to NOT implement the marker interface —
/// see ManagedPluginLoaderTests.ASidecarAndLoadAgreeingOnTheClientButATypeThatDoesNotImplementItIsRefused.
/// Neither can reach a SUCCESSFUL load with client:true, which is what a test proving
/// IPluginContext.Client actually comes back non-null needs.
/// </summary>
public sealed class DeclaresClientPlugin : IPlugin, IPluginClientConsumer
{
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
        Task.FromResult(new PluginManifest("declares-client", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("dc_tool", "a fixture tool",
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
