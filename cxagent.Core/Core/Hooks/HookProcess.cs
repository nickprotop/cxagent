using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CxAgent.Core.Hooks;

/// <summary>One command hook to run, with the input it reads on stdin.</summary>
/// <param name="ProjectDir">The repository (or working folder) the hook runs in and is told about.</param>
public sealed record HookRun(HookDefinition Hook, JsonObject Input, string ProjectDir);

/// <summary>
/// Runs a command hook: its command in a shell, the event's JSON on stdin, and its exit code and output
/// read by the contract's rules.
///
/// <para>A HOOK CANNOT HOLD THE SESSION. It runs on every tool call it matches, written by whoever wrote
/// the hooks file; one that hangs would freeze every call after it, and one that prints without end would
/// fill memory. So each run has a deadline that kills the whole process tree — a backgrounded child
/// included, or it outlives the session holding whatever it opened — and each stream is cut at
/// <see cref="OutputCap"/>.</para>
/// </summary>
public static class HookProcess
{
    /// <summary>The most of each stream kept, in characters.</summary>
    public const int OutputCap = 64 * 1024;

    /// <summary>A hook's deadline when it sets none.</summary>
    public const int DefaultTimeoutSeconds = 60;

    public static async Task<HookReply> RunAsync(HookRun run, CancellationToken ct)
    {
        var hook = run.Hook;
        var start = Shell(hook.Shell, hook.Command ?? "");
        if (start is null) return HookReply.Failed("no shell to run it: install bash, or set \"shell\": \"powershell\"");

        start.WorkingDirectory = run.ProjectDir;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.Environment["CXAGENT_PROJECT_DIR"] = run.ProjectDir;
        // The contract's other spelling of the same variable, so a script written for it finds its folder.
        start.Environment["CLAUDE_PROJECT_DIR"] = run.ProjectDir;

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return HookReply.Failed($"could not start: {ex.Message}");
        }

        var stdout = ReadCapped(process.StandardOutput);
        var stderr = ReadCapped(process.StandardError);

        var timeout = TimeSpan.FromSeconds(hook.TimeoutSeconds ?? DefaultTimeoutSeconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            // WRITTEN AND CLOSED, so a hook that reads stdin to the end sees the end. A hook that never reads
            // it can exit first and break the pipe; that is its choice, not a failure.
            try
            {
                await process.StandardInput.WriteAsync(run.Input.ToJsonString().AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            catch (IOException) { }

            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            ct.ThrowIfCancellationRequested();
            return HookReply.Failed($"timed out after {timeout.TotalSeconds:0} s");
        }

        var output = await stdout;
        var errors = await stderr;
        return Interpret(process.ExitCode, output, errors);
    }

    /// <summary>The contract's exit-code rules.</summary>
    internal static HookReply Interpret(int exitCode, string stdout, string stderr)
    {
        if (exitCode == 2) return new(HookOutcomeKind.Block, stderr.Trim(), null, stdout, stderr);
        if (exitCode != 0)
            return new(HookOutcomeKind.Failed,
                $"exited {exitCode}" + (stderr.Trim() is { Length: > 0 } e ? $": {e}" : ""), null, stdout, stderr);

        // JSON ONLY WHEN IT LOOKS LIKE JSON. Plain text on exit 0 is ordinary output (context, for the events
        // that take it); text that opens an object and does not parse is a hook that meant to answer and
        // could not, which must not read as "no objection".
        var trimmed = stdout.Trim();
        if (!trimmed.StartsWith('{')) return new(HookOutcomeKind.Ok, null, null, stdout, stderr);
        try
        {
            return JsonNode.Parse(trimmed) is JsonObject json
                ? new(HookOutcomeKind.Ok, null, json, stdout, stderr)
                : new(HookOutcomeKind.Failed, "its output is not a JSON object", null, stdout, stderr);
        }
        catch (JsonException ex)
        {
            return new(HookOutcomeKind.Failed, $"its output is not valid JSON: {ex.Message}", null, stdout, stderr);
        }
    }

    /// <summary>
    /// How the command runs: bash unless the hook asks for PowerShell, and PowerShell on Windows when no
    /// bash is installed — the contract's default, so a hooks file behaves the same wherever it is shared.
    /// </summary>
    private static ProcessStartInfo? Shell(string? shell, string command)
    {
        if (shell == "powershell" || (OperatingSystem.IsWindows() && FindBash() is null))
        {
            var ps = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
            var psi = new ProcessStartInfo(ps);
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) psi.ArgumentList.Add(a);
            return psi;
        }
        if (FindBash() is not { } bash) return null;
        var start = new ProcessStartInfo(bash);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        return start;
    }

    private static string? FindBash()
    {
        if (!OperatingSystem.IsWindows()) return "bash";
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("CXAGENT_GIT_BASH_PATH"),
                     @"C:\Program Files\Git\bin\bash.exe",
                     @"C:\Program Files (x86)\Git\bin\bash.exe",
                 })
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>Reads a stream to its end, keeping the first <see cref="OutputCap"/> characters and draining the rest.</summary>
    private static async Task<string> ReadCapped(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        var cut = false;
        int n;
        // DRAINED PAST THE CAP, not abandoned: a writer blocked on a full pipe never exits, and the deadline
        // would report a hook that had finished its work as hung.
        while ((n = await reader.ReadAsync(buffer)) > 0)
        {
            if (sb.Length < OutputCap) sb.Append(buffer, 0, Math.Min(n, OutputCap - sb.Length));
            if (sb.Length >= OutputCap && n > 0) cut = true;
        }
        if (cut) sb.Append($"\n… (output cut at {OutputCap / 1024} KiB)");
        return sb.ToString();
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
