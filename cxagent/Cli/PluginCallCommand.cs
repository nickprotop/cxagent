using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CxAgent.Plugins.Testing;

namespace CxAgent.Cli;

/// <summary>The option values as typed, before any is checked — what <see cref="CallRequest.Parse"/> collects.</summary>
internal sealed record RawCallOptions(string? Settings, string? SettingsFile, string? Answer, string? Timeout,
    string? Command, string? Cwd);

/// <summary>What <c>cxagent plugin call</c> was asked to do, parsed and checked before anything loads.</summary>
internal sealed record CallRequest(string Folder, string? Tool, string? ArgumentsJson, string? Command,
    string CommandArguments, HarnessOptions Options, GateAnswer? Answer, TimeSpan Timeout, bool Json)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The request, or null with <paramref name="error"/> saying what is wrong with the command line.</summary>
    public static CallRequest? Parse(string[] args, out string? error)
    {
        var positionals = new List<string>();
        string? settings = null, settingsFile = null, cwd = null, answer = null, timeout = null, command = null;
        var commandArguments = new List<string>();
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            // EVERYTHING AFTER --command's NAME IS THE COMMAND'S, as a person would type it after /name —
            // EXCEPT --json, the one option a script appends last, which stays the option wherever it is.
            if (command is not null)
            {
                if (args[i] == "--json") json = true;
                else commandArguments.Add(args[i]);
                continue;
            }

            switch (args[i])
            {
                case "--json": json = true; break;
                case "--settings": settings = Value(args, ref i); break;
                case "--settings-file": settingsFile = Value(args, ref i); break;
                case "--cwd": cwd = Value(args, ref i); break;
                case "--answer": answer = Value(args, ref i); break;
                case "--timeout": timeout = Value(args, ref i); break;
                case "--command": command = Value(args, ref i); break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"unknown option '{args[i]}'.";
                        return null;
                    }

                    positionals.Add(args[i]);
                    break;
            }
        }

        error = Check(positionals, new RawCallOptions(settings, settingsFile, answer, timeout, command, cwd));
        if (error is not null) return null;

        var options = new HarnessOptions
        {
            WorkingDirectory = cwd,
            Settings = settings is not null ? ParseElement(settings)
                : settingsFile is not null ? ParseElement(File.ReadAllText(settingsFile)) : null,
        };
        return new CallRequest(positionals[0], command is null ? positionals[1] : null,
            command is null && positionals.Count > 2 ? positionals[2] : null,
            command, string.Join(' ', commandArguments), options,
            answer switch { "allow" => GateAnswer.Allow, "deny" => GateAnswer.Deny, _ => null },
            timeout is null ? DefaultTimeout : TimeSpan.FromSeconds(double.Parse(timeout, CultureInfo.InvariantCulture)),
            json);
    }

    // A MISSING VALUE IS THE NEXT CHECK'S PROBLEM: null here reads as "not given", and Check says so.
    private static string? Value(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : "";

    // THE LONGEST WAIT A CancellationTokenSource TAKES, in seconds — CancelAfter refuses more.
    private static readonly double LongestTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1).TotalSeconds;

    private static string? Check(List<string> positionals, RawCallOptions raw)
    {
        var command = raw.Command;
        var wanted = command is null ? "a plugin folder and a tool" : "a plugin folder";
        if (positionals.Count < (command is null ? 2 : 1)) return $"call takes {wanted}.";
        if (positionals.Count > (command is null ? 3 : 1)) return $"unexpected '{positionals[^1]}'.";
        if (command is "") return "--command takes a command name.";
        if (raw.Settings is not null && raw.SettingsFile is not null) return "give --settings or --settings-file, not both.";
        if (raw.SettingsFile is not null && !File.Exists(raw.SettingsFile)) return $"no settings file '{raw.SettingsFile}'.";
        if (raw.Answer is not null and not "allow" and not "deny") return "--answer is allow or deny.";
        if (raw.Cwd is "") return "--cwd takes a folder.";
        if (raw.Cwd is not null && !Directory.Exists(raw.Cwd)) return $"no folder '{raw.Cwd}' for --cwd.";

        // FINITE AND IN RANGE: "NaN" and "Infinity" parse as doubles, and neither is a wait.
        if (raw.Timeout is not null
            && (!double.TryParse(raw.Timeout, CultureInfo.InvariantCulture, out var s) || !double.IsFinite(s) || s <= 0 || s > LongestTimeout))
            return "--timeout is a number of seconds.";
        if (command is null && positionals.Count > 2 && !IsObject(positionals[2])) return "arguments must be a JSON object.";
        if (raw.Settings is not null && !IsObject(raw.Settings)) return "--settings must be a JSON object.";
        if (raw.SettingsFile is not null && !IsObject(File.ReadAllText(raw.SettingsFile)))
            return $"'{raw.SettingsFile}' must hold a JSON object.";
        return null;
    }

    private static bool IsObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement ParseElement(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}

/// <summary>
/// <c>cxagent plugin call</c> — the real lifecycle once: Load, Start, gate, Invoke, Stop.
///
/// <para>AN UNANSWERED OR DENIED GATE EXITS 0. A session would stop at that prompt too, so stopping is
/// not the plugin's fault; exit 1 is kept for what is — a load that fails, a Start that throws, a
/// call that fails or overruns.</para>
/// </summary>
internal static class PluginCallCommand
{
    // RELAXED ESCAPING: the default encoder writes "21°C" as "21\u00B0C", and this output is read by a
    // person or parsed as JSON — never embedded in HTML, which is what the strict encoder guards.
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (CallRequest.Parse(args, out var error) is not { } request) return PluginCli.Fail(stderr, error!);

        // THE TIMEOUT COVERS START TOO: a plugin that hangs starting is the same overrun as one that
        // hangs answering.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);

        HarnessPlugin plugin;
        try
        {
            plugin = await PluginHarness.LoadAsync(request.Folder, request.Options, timeout.Token).ConfigureAwait(false);
        }
        catch (PluginLoadException ex)
        {
            // check's SHAPE, field and message, so one parser reads both commands' problems.
            if (request.Json)
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    error = "load",
                    problems = ex.Problems.Select(p => new { field = p.Field, message = p.Message }),
                }, Indented));
            else foreach (var p in ex.Problems) stdout.WriteLine(p.ToString());
            return 1;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return TimedOut(stdout, stderr, request);
        }

        // DISPOSED WHATEVER HAPPENS BELOW — a timeout included — so Stop runs and the plugin's
        // processes are reaped rather than outliving the command.
        await using (plugin.ConfigureAwait(false))
        {
            try
            {
                var exit = request.Command is { } name
                    ? await Command(plugin, request, name, stdout, timeout.Token).ConfigureAwait(false)
                    : await Call(plugin, request, stdout, timeout.Token).ConfigureAwait(false);

                // A NATIVE CALL DOES NOT THROW WHEN ITS WAIT IS CANCELLED: AbiPlugin turns it into a
                // failed result. So an overrun is read off the timer, not only off an exception.
                return timeout.IsCancellationRequested && !ct.IsCancellationRequested ? TimedOut(stdout, stderr, request) : exit;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return TimedOut(stdout, stderr, request);
            }
            catch (ArgumentException ex)
            {
                // A TOOL OR COMMAND THE PLUGIN DOES NOT DECLARE: the message names the ones it does.
                stderr.WriteLine($"cxagent plugin: {ex.Message.Split(" (Parameter")[0]}");
                return 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A COMMAND THAT THREW — a tool's throw is already a failed result. The plugin's
                // fault, so exit 1, with what it said rather than a stack trace.
                stderr.WriteLine($"cxagent plugin: the plugin threw: {ex.Message}");
                return 1;
            }
        }
    }

    private static int TimedOut(TextWriter stdout, TextWriter stderr, CallRequest request)
    {
        var seconds = request.Timeout.TotalSeconds;
        stderr.WriteLine($"cxagent plugin: timed out after {seconds.ToString(CultureInfo.InvariantCulture)} s; the plugin was stopped.");

        // ONE OBJECT ON STDOUT UNDER --json, whatever happened, so a script parses it either way.
        if (request.Json) stdout.WriteLine(JsonSerializer.Serialize(new { error = "timeout", seconds }, Indented));
        return 1;
    }

    private static async Task<int> Call(HarnessPlugin plugin, CallRequest request, TextWriter stdout, CancellationToken ct)
    {
        // LIVE, UNLESS --json: one JSON object is then the whole output, so a script parses stdout as it is.
        var streaming = false;
        Action<HarnessReport>? live = request.Json ? null : report =>
        {
            if (report is HarnessReport.Stream s)
            {
                stdout.Write(s.Text);
                streaming = true;
                return;
            }

            if (streaming) { stdout.WriteLine(); streaming = false; }
            stdout.WriteLine(Describe(report));
        };

        var call = await plugin.CallAsync(request.Tool!, request.ArgumentsJson, request.Answer, live, ct).ConfigureAwait(false);
        if (streaming) stdout.WriteLine();

        if (request.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                gate = call.Gate is { } g ? new { display = g.Display, alwaysAskable = g.AlwaysAskable, subject = g.Subject } : null,
                answer = call.Answer?.ToString().ToLowerInvariant(),
                stoppedAtGate = call.StoppedAtGate,
                reports = call.Reports.Select(Describe),
                submits = plugin.Submits,
                result = call.Result is { } r ? Result(r) : null,
            }, Indented));
            return call.Result is { Success: false } ? 1 : 0;
        }

        if (call.Gate is { } gate)
        {
            // THE QUESTION BEFORE THE REPORTS, as a user sees the prompt before the work: printed
            // after the call because the reports were printed live, but always first in a stopped call.
            stdout.WriteLine($"asks: {gate.Display}{(gate.AlwaysAskable ? "" : " (no \"Always\")")}");
            stdout.WriteLine(call.Answer is { } a
                ? $"answered: {a.ToString().ToLowerInvariant()}"
                : "stopped: a user would be asked — pass --answer allow or --answer deny");
        }

        foreach (var goal in plugin.Submits) stdout.WriteLine($"would start a turn: {goal}");
        if (call.Result is { } result) stdout.WriteLine(JsonSerializer.Serialize(Result(result), Indented));
        return call.Result is { Success: false } ? 1 : 0;
    }

    private static async Task<int> Command(HarnessPlugin plugin, CallRequest request, string name, TextWriter stdout, CancellationToken ct)
    {
        var result = await plugin.CommandAsync(name, request.CommandArguments, ct).ConfigureAwait(false);
        var status = result.Status.ToString().ToLowerInvariant();
        if (request.Json) stdout.WriteLine(JsonSerializer.Serialize(new { message = result.Message, status }, Indented));
        else stdout.WriteLine($"{status}: {result.Message ?? "(no message)"}");

        // A REFUSAL IS THE COMMAND DECLINING — a bad argument, a precondition unmet — the command-line
        // equivalent of a call whose result is not a success.
        return result.Status == CxAgent.Core.Plugins.PluginCommandOutcome.Refused ? 1 : 0;
    }

    private static object Result(CxAgent.Core.Models.JobResult r) =>
        new { success = r.Success, exitCode = r.ExitCode, errorMessage = r.ErrorMessage, output = r.Output };

    private static string Describe(HarnessReport report) => report switch
    {
        HarnessReport.Progress p => $"progress {p.Percent.ToString("0", CultureInfo.InvariantCulture)}% {p.Message}".TrimEnd(),
        HarnessReport.Log l => $"log [{l.Level}] {l.Line}",
        HarnessReport.Stream s => $"stream {s.Text}",
        _ => report.ToString(),
    };
}
