using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CxAgent.Core.Llm;
using CxAgent.Core.Mcp;
using CxAgent.Core.Models;

namespace CxAgent.Core.Hooks;

/// <summary>What an <c>agent</c> hook's verifier is given: the model to run on, and the question with the event in it.</summary>
public sealed record HookAgentRequest(ILlmProvider Model, string Prompt, string ProjectDir)
{
    /// <summary>Where the verifier's model spend is recorded.</summary>
    public Action<LlmUsage>? Spend { get; init; }
}

/// <summary>
/// What hooks of the types other than command reach: models by name, MCP servers by name, the network, and a
/// way to run a read-only agent. A session builds one; anything missing makes the hooks that need it fail
/// with a reason, rather than run some other way.
/// </summary>
public sealed record HookServices
{
    /// <summary>A configured <c>provider:model</c> entry by its name, or null. Only the user's own entries: a repository cannot bring one.</summary>
    public Func<string, ILlmProvider?> Model { get; init; } = _ => null;

    /// <summary><c>hooks.defaultModel</c>: the entry a prompt or agent hook uses when it names none.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>A configured MCP server by its name, or null. Only the user's own: a repository cannot bring one.</summary>
    public Func<string, IMcpServer?> McpServer { get; init; } = _ => null;

    public HttpClient Http { get; init; } = SharedHttp;

    /// <summary>Reads an environment variable — for an http hook's allowed header values.</summary>
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>
    /// Records what a hook's model call cost, under a label naming the hook — so a prompt hook firing on every tool
    /// call shows in the session's spend and in /stats, rather than as money nobody can see going.
    /// </summary>
    public Action<LlmUsage, string>? Spend { get; init; }

    /// <summary>Runs an agent hook's verifier and returns its final text; null where no agent can be run.</summary>
    public Func<HookAgentRequest, CancellationToken, Task<string>>? Agent { get; init; }

    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
}

/// <summary>
/// Runs one hook of any type, and turns what it answered into the reply a command hook would have given — so
/// everything after (combining, permissions, failures) treats all five alike.
/// </summary>
public static class HookDispatch
{
    /// <summary>
    /// A hook's deadline: its own <c>timeout</c>, else the contract's default for its type — 600 s for command,
    /// http and mcp_tool (30 s on UserPromptSubmit and model switches, where a person is waiting; 10 s on
    /// MessageDisplay, which runs on every reply), 30 s for a
    /// prompt, 60 s for an agent.
    /// </summary>
    public static int TimeoutSeconds(HookDefinition hook) =>
        hook.TimeoutSeconds ?? hook.Type switch
        {
            "prompt" => 30,
            "agent" => 60,
            _ => hook.Event switch
            {
                HookEvents.UserPromptSubmit => 30,
                // TEN SECONDS where a command waits on them synchronously: a model switch and a config change hold
                // the window until their hooks answer, and a hung default of minutes would freeze it.
                HookEvents.PreModelSwitch or HookEvents.PostModelSwitch or HookEvents.ConfigChange => 10,
                HookEvents.MessageDisplay => 10,
                _ => 600,
            },
        };

    public static async Task<HookReply> RunAsync(HookRun run, HookServices services, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds(run.Hook)));
        try
        {
            return run.Hook.Type switch
            {
                "command" => await HookProcess.RunAsync(run, ct),
                "prompt" => await PromptAsync(run, services, deadline.Token),
                "agent" => await AgentAsync(run, services, deadline.Token),
                "http" => await HttpAsync(run, services, deadline.Token),
                "mcp_tool" => await McpAsync(run, services, deadline.Token),
                _ => HookReply.Failed($"a {run.Hook.Type} hook is not supported here"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return HookReply.Failed($"timed out after {TimeoutSeconds(run.Hook)} s");
        }
        // ANYTHING ELSE A MODEL, A URL OR A SERVER THROWS is this hook failing — a refused key, a bad address, a dropped
        // connection — and fails by the same rule as a crashed command: reported, and blocking only with onFailure.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HookReply.Failed(ex.Message);
        }
    }

    // --- prompt and agent: a model's yes or no -------------------------------------------------------------

    private const string AnswerShape =
        "Answer with one JSON object and nothing else: {\"ok\": true} to allow, or {\"ok\": false, \"reason\": \"why\"} "
        + "to stop it. Add \"impossible\": true if what is asked can never be satisfied.";

    /// <summary>
    /// The model a prompt or agent hook asks: its own, else <c>hooks.defaultModel</c>, else nothing. NEVER THE
    /// SESSION'S: a hook that runs on every tool call, on whichever model the session happens to be on, is a
    /// bill nobody chose.
    /// </summary>
    private static (ILlmProvider? Model, string? Error) ModelFor(HookDefinition hook, HookServices services)
    {
        var name = hook.Model ?? services.DefaultModel;
        if (name is null)
            return (null, "it names no model: set \"model\" on the hook, or hooks.defaultModel in config.json");
        return services.Model(name) is { } model ? (model, null)
            : (null, $"its model \"{name}\" is not one of your configured providers");
    }

    private static string Question(HookRun run)
    {
        var input = run.Input.ToJsonString();
        var prompt = run.Hook.Prompt ?? "";
        return prompt.Contains("$ARGUMENTS", StringComparison.Ordinal) ? prompt.Replace("$ARGUMENTS", input) : prompt + "\n\n" + input;
    }

    private static async Task<HookReply> PromptAsync(HookRun run, HookServices services, CancellationToken ct)
    {
        var (model, error) = ModelFor(run.Hook, services);
        if (model is null) return HookReply.Failed(error!);
        var response = await model.ChatAsync(
        [
            new ChatMessage { Role = "system", Content = "You check one event for an agent's hook. " + AnswerShape },
            new ChatMessage { Role = "user", Content = Question(run) },
        ], null, ct);
        if (response.Usage is { } usage) services.Spend?.Invoke(usage, $"hook:{run.Hook.DisplayName}");
        return FromAnswer(run.Hook, response.Text ?? "", carryOn: run.Hook.ContinueOnBlock);
    }

    private static async Task<HookReply> AgentAsync(HookRun run, HookServices services, CancellationToken ct)
    {
        var (model, error) = ModelFor(run.Hook, services);
        if (model is null) return HookReply.Failed(error!);
        if (services.Agent is not { } agent) return HookReply.Failed("agent hooks cannot run here");
        var text = await agent(new HookAgentRequest(model, Question(run) + "\n\n" + AnswerShape, run.ProjectDir)
            { Spend = usage => services.Spend?.Invoke(usage, $"hook:{run.Hook.DisplayName}") }, ct);
        // A VERIFIER STOPPED BY ITS DEADLINE returns what it had rather than throwing; that is a timeout, not an answer.
        ct.ThrowIfCancellationRequested();
        // AN AGENT HOOK ALWAYS CARRIES ON when it blocks, as the contract has it: it looked before it answered,
        // and its reason is worth handing back.
        return FromAnswer(run.Hook, text, carryOn: true);
    }

    /// <summary>
    /// A model's <c>{ok, reason, impossible}</c> as the reply a command hook would give: ok is no objection;
    /// not ok is a block, which ends the turn unless the hook carries on — and on Stop it never ends it,
    /// since a Stop block is "keep working", unless the model says the condition is impossible.
    /// </summary>
    private static HookReply FromAnswer(HookDefinition hook, string text, bool carryOn)
    {
        if (LastObject(text) is not { } answer || answer["ok"] is not JsonValue okValue || !okValue.TryGetValue<bool>(out var ok))
            return HookReply.Failed($"the model did not answer with {{\"ok\": …}}: {Clip(text)}");
        if (ok) return new(HookOutcomeKind.Ok, null, null, text, "");

        var reason = (string?)answer["reason"] ?? "the hook's model said no";
        var impossible = answer["impossible"] is JsonValue iv && iv.TryGetValue<bool>(out var imp) && imp;
        // WHERE A NO HAS NO MEANING, it is no objection: a permission request is answered by command hooks, and
        // a Stop that can never be satisfied lets the turn end.
        if (hook.Event == HookEvents.PermissionRequest || (impossible && hook.Event is HookEvents.Stop or HookEvents.SubagentStop))
            return new(HookOutcomeKind.Ok, null, null, text, "");

        // AFTER A BATCH, A NO THAT CARRIES ON IS WORD FOR THE MODEL, not a stop: "the tests fail now" should reach it
        // before its next call, as the contract has it for an agent hook or a prompt hook set to carry on.
        if (carryOn && hook.Event == HookEvents.PostToolBatch)
            return new(HookOutcomeKind.Ok, null, new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = hook.Event, ["additionalContext"] = reason },
            }, text, "");

        var json = new JsonObject { ["decision"] = "block", ["reason"] = reason };
        var endsTurn = !carryOn && hook.Event is HookEvents.PreToolUse or HookEvents.PostToolUse or HookEvents.UserPromptSubmit;
        if (endsTurn) { json["continue"] = false; json["stopReason"] = reason; }
        return new(HookOutcomeKind.Ok, null, json, text, "");
    }

    /// <summary>The last JSON object in a model's text — models wrap their answer in words more often than not.</summary>
    private static JsonObject? LastObject(string text)
    {
        for (var end = text.LastIndexOf('}'); end >= 0; end = end == 0 ? -1 : text.LastIndexOf('}', end - 1))
            for (var start = text.LastIndexOf('{', end); start >= 0; start = start == 0 ? -1 : text.LastIndexOf('{', start - 1))
            {
                try { if (JsonNode.Parse(text[start..(end + 1)]) is JsonObject o) return o; }
                catch (JsonException) { }
            }
        return null;
    }

    // --- http ------------------------------------------------------------------------------------------

    private static readonly Regex EnvRef = new(@"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?", RegexOptions.CultureInvariant);

    private static async Task<HookReply> HttpAsync(HookRun run, HookServices services, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, run.Hook.Url)
        {
            Content = new StringContent(run.Input.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in run.Hook.Headers ?? new Dictionary<string, string>())
        {
            // ONLY THE NAMED VARIABLES EXPAND. A header naming one it was not allowed reads empty: a repository's
            // hook must not be able to post the user's environment to a URL of its choosing.
            // AND ONLY FOR YOUR OWN HOOKS: a repository's hook lists its own allowedEnvVars, so honouring them would let
            // it name the variable it wants sent. A repository's http hook expands nothing.
            var mayExpand = run.Hook.Source is HookSource.User or HookSource.Library;
            var expanded = EnvRef.Replace(value, m =>
                mayExpand && run.Hook.AllowedEnvVars.Contains(m.Groups[1].Value) ? services.Env(m.Groups[1].Value) ?? "" : "");
            request.Headers.TryAddWithoutValidation(name, expanded);
        }

        HttpResponseMessage response;
        try { response = await services.Http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { return HookReply.Failed($"could not reach {run.Hook.Url}: {ex.Message}"); }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return HookReply.Failed($"{run.Hook.Url} answered {(int)response.StatusCode}");
            if (string.IsNullOrWhiteSpace(body)) return new(HookOutcomeKind.Ok, null, null, "", "");
            // A 2XX WITH TEXT THAT IS NOT JSON is the endpoint not speaking the contract — reported, never read as a yes.
            return body.TrimStart().StartsWith('{')
                ? HookProcess.Interpret(0, body, "")
                : HookReply.Failed($"{run.Hook.Url} answered text that is not JSON: {Clip(body)}");
        }
    }

    // --- mcp_tool --------------------------------------------------------------------------------------

    private static readonly Regex PathRef = new(@"\$\{([^}]+)\}", RegexOptions.CultureInvariant);

    private static async Task<HookReply> McpAsync(HookRun run, HookServices services, CancellationToken ct)
    {
        if (services.McpServer(run.Hook.Server!) is not { } server)
            return HookReply.Failed($"no MCP server named \"{run.Hook.Server}\" is configured and running");

        var args = run.Hook.Input is { } input ? Substitute(JsonNode.Parse(input.GetRawText())!, run.Input) : new JsonObject();
        var text = await server.CallToolAsync(run.Hook.Tool!, JsonSerializer.SerializeToElement(args), ct);
        if (text.StartsWith("error:", StringComparison.Ordinal)) return HookReply.Failed($"{run.Hook.Server}.{run.Hook.Tool}: {text[6..].Trim()}");
        // THE TOOL'S TEXT READS AS A COMMAND'S STDOUT ON EXIT 0: JSON is an answer, anything else is no objection.
        return HookProcess.Interpret(0, text, "");
    }

    /// <summary>The tool's arguments with every string <c>${a.b}</c> replaced by that path's value in the event.</summary>
    private static JsonNode Substitute(JsonNode node, JsonObject input) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, kv.Value is null ? null : (JsonNode?)Substitute(kv.Value, input)))),
        JsonArray a => new JsonArray([.. a.Select(v => v is null ? null : Substitute(v, input))]),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(PathRef.Replace(s, m => Lookup(input, m.Groups[1].Value)))!,
        _ => node.DeepClone(),
    };

    private static string Lookup(JsonObject input, string path)
    {
        JsonNode? at = input;
        foreach (var part in path.Split('.')) at = (at as JsonObject)?[part];
        return at switch { null => "", JsonValue v when v.TryGetValue<string>(out var s) => s, _ => at.ToJsonString() };
    }

    private static string Clip(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
