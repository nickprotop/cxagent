using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CXP_ROOT_NAMESPACE;

/// <summary>
/// One tool with a per-call gate: the sidecar beside this file is the manifest, and Gate decides
/// per call whether to ask. Replace greet with your own tool.
/// </summary>
public sealed class Plugin : IPlugin, IPluginGateSource
{
    // THE SIDECAR IS THE ONE COPY: what Load returns is the file the user approved.
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
        Task.FromResult(PluginManifest.FromSidecar(this));

    public Task Start(CancellationToken ct) => Task.CompletedTask;

    // ASKED ONLY WHEN IT MATTERS: null means no question; a PluginGate carries the words a user reads.
    public PluginGate? Gate(string toolName, JobParameters call) =>
        call.Get<string>("name") is "admin" ? new PluginGate("greet admin") : null;

    public async Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct)
    {
        var name = call.Get<string>("name");
        if (string.IsNullOrWhiteSpace(name))
            return new JobResult { Success = false, ErrorMessage = "pass a name to greet." };

        context.ReportProgress(50, "greeting");
        await Task.Yield();
        return new JobResult { Success = true, Output = { ["content"] = $"Hello, {name}!" } };
    }

    public Task Stop(CancellationToken ct) => Task.CompletedTask;
}
