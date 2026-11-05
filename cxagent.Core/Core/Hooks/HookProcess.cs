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

    /// <summary>
    /// The input as text a script can grep: non-ASCII and quotes as themselves, not <c>\u</c> escapes —
    /// valid JSON either way, but a hook matching a path with <c>grep</c> would miss an escaped one.
    /// </summary>
    private static readonly JsonSerializerOptions Plain = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };


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
        // UTF-8 WITHOUT A BOM: the default is the console's code page (and on some hosts plain ASCII), which
        // mangles a path or prompt with any non-ASCII character before the script sees it.
        start.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        start.Environment["CXAGENT_PROJECT_DIR"] = run.ProjectDir;
        // The contract's other spelling of the same variable, so a script written for it finds its folder.
        start.Environment["CLAUDE_PROJECT_DIR"] = run.ProjectDir;

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return HookReply.Failed($"could not start: {ex.Message}");
        }

        var stdout = new CappedReader(process.StandardOutput);
        var stderr = new CappedReader(process.StandardError);

        var timeout = TimeSpan.FromSeconds(HookDispatch.TimeoutSeconds(hook));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            // WRITTEN AND CLOSED, so a hook that reads stdin to the end sees the end. A hook that never reads
            // it can exit first and break the pipe; that is its choice, not a failure.
            try
            {
                await process.StandardInput.WriteAsync(run.Input.ToJsonString(Plain).AsMemory(), deadline.Token);
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

        // THE HOOK HAS EXITED, BUT ITS PIPES MAY NOT CLOSE. A child it started in the background (`server &`)
        // inherits stdout and holds it open for as long as it lives; waiting for end-of-stream would hold the
        // tool call, the turn and Esc with it. So the output gets a short grace to drain, and then what was
        // read is what the hook said. The child is left running — starting it was the hook's intent.
        try { await Task.WhenAll(stdout.Done, stderr.Done).WaitAsync(DrainGrace, ct); }
        catch (TimeoutException) { }

        return Interpret(process.ExitCode, stdout.Text, stderr.Text);
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

    /// <summary>How long a finished hook's output may take to drain before what was read is taken as all of it.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reads a stream to its end in the background, keeping the first <see cref="OutputCap"/> characters, and
    /// can say what it has so far at any moment — so a pipe that never closes still yields what came through.
    /// </summary>
    private sealed class CappedReader
    {
        private readonly StringBuilder _sb = new();
        private bool _cut;

        public CappedReader(StreamReader reader) => Done = ReadAsync(reader);

        public Task Done { get; }

        public string Text
        {
            get
            {
                lock (_sb) return _cut ? _sb + $"\n… (output cut at {OutputCap / 1024} KiB)" : _sb.ToString();
            }
        }

        private async Task ReadAsync(StreamReader reader)
        {
            var buffer = new char[8192];
            int n;
            try
            {
                // DRAINED PAST THE CAP, not abandoned: a writer blocked on a full pipe never exits, and the
                // deadline would report a hook that had finished its work as hung.
                while ((n = await reader.ReadAsync(buffer)) > 0)
                    lock (_sb)
                    {
                        if (_sb.Length < OutputCap) _sb.Append(buffer, 0, Math.Min(n, OutputCap - _sb.Length));
                        if (_sb.Length >= OutputCap) _cut = true;
                    }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
