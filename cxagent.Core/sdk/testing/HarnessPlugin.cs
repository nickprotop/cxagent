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
    private bool _disposed;

    internal HarnessPlugin(IPlugin instance, PluginManifest manifest, HarnessContext context, RecordingClient? client)
    {
        _instance = instance;
        Manifest = manifest;
        _context = context;
        _client = client;
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
        var result = await _instance.Invoke(tool, call, context, ct).ConfigureAwait(false);
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
        try
        {
            await _instance.Stop(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StopError = ex.Message;
        }
        finally
        {
            _context.Dispose();
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
