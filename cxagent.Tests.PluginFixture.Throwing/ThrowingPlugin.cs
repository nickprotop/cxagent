using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests.PluginFixture.Throwing;

/// <summary>Throws from Load — proves a plugin's own failure is reported by ManagedPluginLoader
/// rather than propagating as an unhandled exception out of a loader a caller expects to fail
/// gracefully.</summary>
public sealed class ThrowingPlugin : IPlugin
{
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
        throw new InvalidOperationException("fixture: this plugin always fails to load");

    public Task Start(CancellationToken ct) => Task.CompletedTask;

    public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct) =>
        Task.FromResult(new JobResult { Success = true });

    public Task Stop(CancellationToken ct) => Task.CompletedTask;
}
