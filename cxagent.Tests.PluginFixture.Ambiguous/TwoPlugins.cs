using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests.PluginFixture.Ambiguous;

public sealed class FirstPlugin : IPlugin
{
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
        Task.FromResult(new PluginManifest("first", "1.0.0", null, false, []));
    public Task Start(CancellationToken ct) => Task.CompletedTask;
    public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct) =>
        Task.FromResult(new JobResult { Success = true });
    public Task Stop(CancellationToken ct) => Task.CompletedTask;
}

public sealed class SecondPlugin : IPlugin
{
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
        Task.FromResult(new PluginManifest("second", "1.0.0", null, false, []));
    public Task Start(CancellationToken ct) => Task.CompletedTask;
    public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct) =>
        Task.FromResult(new JobResult { Success = true });
    public Task Stop(CancellationToken ct) => Task.CompletedTask;
}
