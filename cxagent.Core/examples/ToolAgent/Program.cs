using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using ToolAgent;

// CONSUMER-INJECTED TOOLS, and nothing else.
//
// SpectreAgent shows that a front end can be small. This one shows the other extension point: three
// tools this example owns, offered to the model beside cxagent's built-ins, each demonstrating a
// different answer to "does this call need a human".
//
// Plain Console on purpose — no Spectre, no markup. What is worth reading here is the permission
// flow, and a second rendering library would be noise around it.

var workingDir = args.FirstOrDefault() ?? Directory.GetCurrentDirectory();
var configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
    ? Path.Combine(x, "cxagent")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "cxagent");

var resolution = ConfigResolver.Resolve(new AppPaths(configDir), Environment.GetEnvironmentVariables()
    .Cast<System.Collections.DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? ""), useMock: false);

if (!resolution.HasProvider)
{
    Console.Error.WriteLine("No provider configured. Run cxagent once to set one up.");
    return 1;
}

// THE GATE ASKS BOTH QUESTIONS THROUGH ONE HOOK, and the prompt cannot tell them apart — nor
// should it. "May show_diff run in this folder" and "may it run THIS call" arrive here identically,
// which is the point: the tool decides what it is asking, the human answers, and neither has to
// know which of the two gates produced the question.
using var manager = SessionManager.Create(new AppPaths(configDir), buildGate: store =>
    PermissionDecider.WithPrompt(store,
        notice: Console.WriteLine,
        promptHook: (request, offerTrust, ct) =>
        {
            Console.WriteLine();
            Console.WriteLine($"  {request.Kind}: {request.Display}");

            // NO "ALWAYS" WHEN AlwaysRule IS NULL. The button is absent rather than present and
            // quietly ignored — notify's calls cannot be generalised, and offering to remember one
            // would promise something the rule system cannot keep.
            Console.Write(request.AlwaysRule is null
                ? "  [o]nce / [d]eny: "
                : $"  [o]nce / [a]lways ({request.AlwaysRule}) / [d]eny: ");

            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            return Task.FromResult(answer switch
            {
                "o" => PermissionChoice.Once,

                // GUARDED, not trusted to the prompt above. The two must agree, and a bare "a"
                // typed at a prompt that never offered it would otherwise store a rule that
                // request said could not be honestly generalised.
                "a" when request.AlwaysRule is not null => PermissionChoice.Always,
                _ => PermissionChoice.Deny,
            });
        }));

var policy = new PermissionPolicy(workingDir, manager.Rules!, EditMode.AlwaysAsk);

// THE INJECTION POINT. SessionFactory wraps each of these in GatedAgentTool on the way through, so
// nothing here has to remember to — a bare tool in this list would otherwise run with no gate at
// all, silently.
var session = manager.Open(workingDir, resolution,
    new SessionPorts
    {
        Observer = new ConsoleSink(),
        ToolObserver = new NullToolSink(),
        Policy = policy,
        Tools = [new Tools.Calc(), new Tools.Deploy(), new Tools.Notify()],
    });

Console.WriteLine($"tools: calc (never asks) · deploy (asks per environment) · notify (asks every time)");
Console.WriteLine("blank line to quit");
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    var prompt = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(prompt)) break;

    if (session.Submit(prompt) is not Session.SubmitOutcome.Started started)
    {
        Console.WriteLine("busy");
        continue;
    }

    await started.Turn;
    Console.WriteLine();
}

return 0;

/// <summary>Where the session's words go — the minimum an embedder must supply.</summary>
internal sealed class ConsoleSink : ISessionObserver
{
    public void UserTurnAdded(ChatMessageId id, string text) { }
    public void AssistantTurnBegan(ChatMessageId id) { }
    public void AssistantTextAppended(ChatMessageId id, string token) => Console.Write(token);
    public void AssistantReasoningAppended(ChatMessageId id, string text) { }
    public void AssistantTurnEnded(ChatMessageId id) => Console.WriteLine();
    public void AssistantLabelled(ChatMessageId id, string header) { }
    public void Failed(string message) => Console.Error.WriteLine(message);
    public void Said(string message) => Console.WriteLine(message);
}

/// <summary>
/// Tool activity, ignored.
///
/// <para>REQUIRED EVEN SO. The port is non-nullable because a session always reports what its tools
/// are doing; an embedder that does not care says so explicitly here rather than by passing null,
/// which would make "nobody is listening" indistinguishable from "this was never wired".</para>
/// </summary>
internal sealed class NullToolSink : IToolObserver
{
    public void ToolsChanged(IReadOnlyList<Job> jobs) { }
    public void ToolUpdated(Job job) { }
    public void ToolProgressed(Job job) { }
    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
    public void ToolOutputAppended(string jobId, string delta) { }
}
