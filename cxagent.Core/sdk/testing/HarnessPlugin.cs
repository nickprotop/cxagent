using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.Testing;

/// <summary>
/// A plugin loaded and started by <see cref="PluginHarness.LoadAsync"/>. Disposing it stops the plugin
/// and kills whatever processes it registered.
/// </summary>
public sealed class HarnessPlugin : IAsyncDisposable
{
    private readonly IPlugin _instance;
    private readonly HarnessContext _context;
    private readonly RecordingClient? _client;
    private readonly TimeSpan _stopTimeout;
    private bool _disposed;

    internal HarnessPlugin(IPlugin instance, PluginManifest manifest, HarnessContext context, RecordingClient? client,
        TimeSpan stopTimeout)
    {
        _instance = instance;
        Manifest = manifest;
        _context = context;
        _client = client;
        _stopTimeout = stopTimeout;
    }

    /// <summary>What the plugin's code returned from Load — already checked against its sidecar.</summary>
    public PluginManifest Manifest { get; }

    /// <summary>The plugin's own log: what it wrote to <c>IPluginContext.Logger</c>, outside any call.</summary>
    public IReadOnlyList<string> Logged => _context.Logged;

    /// <summary>Every goal the plugin submitted. Nothing starts: a submit is recorded and accepted.</summary>
    public IReadOnlyList<string> Submits => _client?.Goals ?? [];

    /// <summary>Why Stop failed when this was disposed, or null.</summary>
    public string? StopError { get; private set; }

    /// <summary>
    /// One call to <paramref name="tool"/>, decided and run as a session would: the gate first, then —
    /// only when the call asks nothing or <paramref name="answer"/> is Allow — the plugin's Invoke.
    /// </summary>
    /// <param name="tool">A tool the manifest declares.</param>
    /// <param name="args">The arguments: an object serialised as the model's JSON, a JSON string, or a JsonElement.</param>
    /// <param name="answer">What the user would click; without one a call that asks goes no further.</param>
    /// <param name="onReport">Each report as it happens, before the call returns.</param>
    /// <param name="ct">Cancels the call, as a cancelled turn would.</param>
    public async Task<HarnessCall> CallAsync(string tool, object? args = null, GateAnswer? answer = null,
        Action<HarnessReport>? onReport = null, CancellationToken ct = default)
    {
        var declared = Manifest.Tools.FirstOrDefault(t => t.Name == tool)
            ?? throw new ArgumentException($"'{Manifest.Name}' has no tool '{tool}'; it declares "
                + (Manifest.Tools.Count == 0 ? "none" : string.Join(", ", Manifest.Tools.Select(t => t.Name))) + ".",
                nameof(tool));

        var call = Parameters(args);
        var question = PluginGateQuestion.For(Manifest, declared, _instance, call) is { } asked
            ? new GateQuestion(asked.Display, asked.AlwaysRule is not null, asked.Subject)
            : null;

        // A USER WOULD BE ASKED, and has not said yes: the call goes no further, as in a session
        // waiting on — or refused at — its prompt.
        if (question is not null && answer is not GateAnswer.Allow)
            return new HarnessCall(question, Asked: true, answer, Result: null, Reports: []);

        var context = new RecordingJobContext(_context.WorkingDirectory, onReport);
        JobResult result;
        try
        {
            // WAITASYNC, AS A SESSION WAITS: a cancelled call stops being waited on whether or not the
            // plugin reads its token, so a plugin stuck in a blocking call cannot hold the caller.
            result = await _instance.Invoke(tool, call, context, ct).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A TOOL THAT THROWS IS A FAILED CALL, as the session's agent records it — not an
            // exception out of the harness that a session would never raise.
            result = new JobResult { Success = false, ErrorMessage = ex.Message };
        }

        return new HarnessCall(question, question is not null, answer, result, context.Reports);
    }

    /// <summary>Runs a command the manifest declares, as a person typing <c>/name arguments</c> would.</summary>
    public Task<CommandResult> CommandAsync(string name, string arguments = "", CancellationToken ct = default)
    {
        if (Manifest.Commands.All(c => c.Name != name))
            throw new ArgumentException($"'{Manifest.Name}' has no command '{name}'; it declares "
                + (Manifest.Commands.Count == 0 ? "none" : string.Join(", ", Manifest.Commands.Select(c => c.Name))) + ".",
                nameof(name));

        // THE LOADERS REFUSE a plugin that declares commands without a handler, and AbiPlugin is one
        // for a native library — so a declared command always has somewhere to go.
        return ((IPluginCommandHandler)_instance).RunCommand(name, arguments, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _context.EndLifetime();
        StopError = await StopWithin(_instance, _stopTimeout).ConfigureAwait(false);
        _context.Dispose();
    }

    /// <summary>
    /// Stops <paramref name="instance"/>, giving up after <paramref name="limit"/>; returns why it
    /// failed, or null. A STOP THAT NEVER RETURNS IS ABANDONED, not awaited: the processes the plugin
    /// registered are still killed after it, and a hung plugin costs its limit rather than the run.
    /// </summary>
    internal static async Task<string?> StopWithin(IPlugin instance, TimeSpan limit)
    {
        using var deadline = new CancellationTokenSource(limit);
        try
        {
            await instance.Stop(deadline.Token).WaitAsync(limit).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && deadline.IsCancellationRequested))
        {
            return $"Stop did not return within {limit.TotalSeconds:0.#} s; it was abandoned.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // THE SHAPE A SESSION HANDS A TOOL: a dictionary of JsonElement values, as the model's JSON arrives.
    // A plain CLR value would let a plugin pass here and fail in a session, where Get<T> converts.
    internal static JobParameters Parameters(object? args)
    {
        var element = args switch
        {
            null => JsonSerializer.SerializeToElement(new { }),
            JsonElement e => e,
            string s => ParseJson(s),
            _ => JsonSerializer.SerializeToElement(args),
        };
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments must be a JSON object.", nameof(args));
        return new JobParameters(element.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));
    }

    private static JsonElement ParseJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"arguments must be a JSON object: {ex.Message}", nameof(text), ex);
        }
    }
}
