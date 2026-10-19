using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests.PluginFixture.Harness;

/// <summary>
/// One plugin with one of everything the harness must handle: a per-call gate (echo), reports during
/// a call (echo), a submit (submit), a call that never returns on its own (slow) and one that ignores
/// its token too (stuck), a call that throws (throws), commands, a Load that needs the client it
/// declared, and a Start and Stop that misbehave when its settings say so.
/// </summary>
public sealed class HarnessPlugin : IPlugin, IPluginGateSource, IPluginClientConsumer, IPluginCommandHandler
{
    private IPluginContext? _context;

    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        // THE CLIENT IT DECLARED, OR NO LOAD — as a plugin that submits at start would need it.
        if (context.Client is null) throw new InvalidOperationException("fixture: declared a client and was given none");
        _context = context;
        return Task.FromResult(PluginManifest.FromSidecar(this));
    }

    private bool Setting(string name) => _context!.Settings.TryGetProperty(name, out var v) && v.GetBoolean();

    public Task Start(CancellationToken ct)
    {
        if (Setting("failStart")) throw new InvalidOperationException("fixture: start refused");
        if (Setting("startCancels")) throw new OperationCanceledException("fixture: start cancelled itself");
        if (Setting("spawnThenFail"))
        {
            // A START THAT FAILS HALF WAY, holding a process: the host must still reap it. The pid is
            // written where the test can read it back.
            var child = System.Diagnostics.Process.Start("sleep", "60")!;
            _context!.RegisterChildProcess(child.Id);
            File.WriteAllText(Path.Combine(_context.WorkingDirectory, "child.pid"), child.Id.ToString());
            throw new InvalidOperationException("fixture: start failed after spawning");
        }

        return Task.CompletedTask;
    }

    public PluginGate? Gate(string toolName, JobParameters call) =>
        toolName == "echo" ? new PluginGate($"echo {call.Get<string>("text")}") { Subject = call.Get<string>("text") } : null;

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
            case "stuck":
                // DELIBERATELY DEAF TO ITS TOKEN, as a plugin with a blocking call inside would be.
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
                return new JobResult { Success = true };
            case "throws":
                throw new InvalidOperationException("fixture: the tool threw");
            default:
                return new JobResult { Success = false, ErrorMessage = $"no tool {toolName}" };
        }
    }

    public Task<CommandResult> RunCommand(string name, string arguments, CancellationToken ct) =>
        name == "harness-throw"
            ? throw new InvalidOperationException("fixture: the command threw")
            : Task.FromResult(new CommandResult($"pong {arguments}".TrimEnd(), PluginCommandOutcome.Reported));

    public async Task Stop(CancellationToken ct)
    {
        // A STOP THAT ENDS WHEN THE LIFETIME DOES — a background loop told to finish through it, then
        // awaited — returns only if the host cancels the lifetime before it waits on Stop.
        if (Setting("stopWaitsOnLifetime"))
            await Task.Delay(Timeout.Infinite, _context!.Lifetime).ContinueWith(_ => { }, TaskScheduler.Default);
        if (Setting("stopHangs"))
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }
}
