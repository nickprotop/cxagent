using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests.PluginFixture.Harness;

/// <summary>
/// One plugin with one of everything the harness must handle: a per-call gate (echo), reports during
/// a call (echo), a submit (submit), a call that never returns on its own (slow), a command, and a
/// Start that refuses when its settings say so.
/// </summary>
public sealed class HarnessPlugin : IPlugin, IPluginGateSource, IPluginClientConsumer, IPluginCommandHandler
{
    private IPluginContext? _context;

    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        return Task.FromResult(PluginManifest.FromSidecar(this));
    }

    public Task Start(CancellationToken ct) =>
        _context!.Settings.TryGetProperty("failStart", out var fail) && fail.GetBoolean()
            ? throw new InvalidOperationException("fixture: start refused")
            : Task.CompletedTask;

    public PluginGate? Gate(string toolName, JobParameters call) =>
        toolName == "echo" ? new PluginGate($"echo {call.Get<string>("text")}") : null;

    public async Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct)
    {
        switch (toolName)
        {
            case "echo":
                var text = call.Get<string>("text") ?? "";
                context.ReportProgress(50, "half");
                context.Log(JobLogLevel.Info, "working");
                context.ReportTextDelta(text);
                return new JobResult { Success = true, Output = { ["content"] = text } };
            case "submit":
                await _context!.Client!.Submit("tidy the folder", ct: ct);
                return new JobResult { Success = true, Output = { ["content"] = "submitted" } };
            case "slow":
                await Task.Delay(Timeout.Infinite, ct);
                return new JobResult { Success = true };
            default:
                return new JobResult { Success = false, ErrorMessage = $"no tool {toolName}" };
        }
    }

    public Task<CommandResult> RunCommand(string name, string arguments, CancellationToken ct) =>
        Task.FromResult(new CommandResult($"pong {arguments}".TrimEnd(), PluginCommandOutcome.Reported));

    public Task Stop(CancellationToken ct) => Task.CompletedTask;
}
