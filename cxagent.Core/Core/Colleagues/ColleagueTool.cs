using System.Text;
using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Colleagues;

/// <summary>
/// <c>colleague</c> — the lead lists the colleagues, brings one in, and keeps a worker as one when the user says yes.
///
/// <para>AGENT-OWNED, like <c>memory</c>: it answers from the session's desk, not from an executor, and sits in the
/// dispatch chain beside it. THE LEAD'S ONLY: a worker cannot keep or bring workers, as it cannot spawn them.</para>
///
/// <para>KEEPING ASKS THE USER, EVERY TIME, through the question the model's own <c>ask_user</c> uses — not the
/// permission gate, whose modes and classifier may answer for the user. A colleague outlives the session and goes
/// into every project it is brought to; that is the user's decision, never a rule's.</para>
///
/// <para>OFFERED WHENEVER COLLEAGUES ARE ON, even with an empty library: keeping the first one needs it, and a tool list
/// that changed mid-session would move the cached prefix.</para>
/// </summary>
/// <param name="desk">The session's desk.</param>
/// <param name="ask">How the lead asks the user, or null where there is no user — keeping is then refused.</param>
/// <param name="parentId">The lead's agent id now, so a brought-in worker's report finds it.</param>
public sealed class ColleagueTool(ColleagueDesk desk,
    Func<IReadOnlyList<UserQuestion>, CancellationToken, Task<QuestionAnswers>>? ask, Func<string> parentId)
{
    public const string ToolName = "colleague";

    private const string KeepAnswer = "Keep";

    public ToolDefinition Definition => new(
        ToolName,
        "Colleagues are workers the user chose to keep across sessions; each starts from what it learned before. "
        + "'list' names them. 'bring' starts one here as a worker on a task — prefer it to a fresh agent when the task "
        + "touches a codebase it knows; afterwards reach it with agent_send, task_output and task_stop like any worker. "
        + "'keep' makes one of this session's workers a colleague; it asks the user first — use it when the user asks, "
        + "or offer it when a worker built up knowledge worth keeping.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": { "type": "string", "enum": ["list", "bring", "keep"] },
                "name": { "type": "string", "description": "bring: the colleague. keep: the name to give it (optional — one is taken from its work)." },
                "task": { "type": "string", "description": "bring: what it should do, as you would brief a worker. Leave it out to have it join idle and give it work later with agent_send." },
                "model": { "type": "string", "description": "bring: a provider entry to run it on instead of its own (optional)." },
                "background": { "type": "boolean", "description": "bring: run in the background and report when done (default true)." },
                "worker": { "type": "string", "description": "keep: the worker's handle." }
              },
              "required": ["action"]
            }
            """).RootElement.Clone());

    /// <summary>Runs the call, or returns null if it is not this tool's.</summary>
    public Task<string?> TryInvokeAsync(ToolCall call, CancellationToken ct) => TryInvokeAsync(call, null, ct);

    /// <summary>
    /// Runs the call, or returns null if it is not this tool's. <paramref name="onChild"/> is told a brought-in
    /// colleague the moment it is built — the hook the agent tool's spawn row uses, so a bring is drawn the same way.
    /// </summary>
    public async Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild, CancellationToken ct)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal)) return null;
        try
        {
            return Arg(call, "action")?.Trim().ToLowerInvariant() switch
            {
                "list" => List(),
                "bring" => await BringAsync(call, onChild, ct),
                "keep" => await KeepAsync(call, ct),
                _ => "colleague needs an action: list, bring or keep.",
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"colleague could not reach the library: {e.Message}";
        }
    }

    private string List()
    {
        var all = desk.List();
        if (all.Count == 0) return "There are no colleagues yet. A worker of this session can be kept as one — 'keep' asks the user.";
        var sb = new StringBuilder();
        foreach (var line in all.Select(ColleagueLine.Of))
            sb.Append(line.Name).Append(line.Description.Length > 0 ? " — " + line.Description : "")
              .Append(line.Knows.Count > 0 ? " · knows: " + string.Join(", ", line.Knows) : "").Append('\n');
        return sb.ToString().TrimEnd();
    }

    private Task<string> BringAsync(ToolCall call, Action<SubAgent>? onChild, CancellationToken ct)
    {
        var name = Arg(call, "name")?.Trim();
        var task = Arg(call, "task")?.Trim();
        if (string.IsNullOrEmpty(name)) return Task.FromResult("bring needs the colleague's name.");
        // NO TASK JOINS IT IDLE, to be given work by agent_send.
        return desk.BringAsync(new BringOrder(name, task ?? "", Arg(call, "model")?.Trim(), Args.ReadBool(call, "background") ?? true, parentId())
        {
            OnChild = onChild,
        }, ct);
    }

    private async Task<string> KeepAsync(ToolCall call, CancellationToken ct)
    {
        var worker = Arg(call, "worker")?.Trim();
        if (string.IsNullOrEmpty(worker)) return "keep needs the worker's handle.";
        var name = Arg(call, "name")?.Trim();

        // REFUSED BEFORE ASKED: a question whose answer could only be refused — a worker that is already the colleague,
        // one that does not exist — wastes the user's attention and teaches them to click without reading.
        if (desk.KeepRefusal(new KeepOrder(worker, name)) is { } refused) return refused;
        if (ask is null) return "Keeping a colleague needs the user's yes, and there is no user to ask here.";

        var asked = name is { Length: > 0 }
            ? $"Keep {worker} as a colleague named {name}?"
            : $"Keep {worker} as a colleague? It is named from its work.";
        var question = new UserQuestion(
            asked + " It goes into the library, keeps learning from its work, and can be brought into any session.",
            "Keep colleague",
            [new QuestionOption(KeepAnswer, "Keep it in the library"), new QuestionOption("Don't keep", "Leave it an ordinary worker")]);
        var answer = await ask([question], ct);
        if (answer.Cancelled || answer.Answers.Count == 0 || answer.Answers[0] != KeepAnswer) return "The user did not keep it.";
        return desk.Keep(new KeepOrder(worker, name));
    }

    private static string? Arg(ToolCall call, string key) =>
        call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
