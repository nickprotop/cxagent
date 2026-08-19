using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;

// AN AGENT THAT CANNOT CHANGE ANYTHING, and cannot delegate.
//
// ToolAgent shows how to ADD a tool. This shows the other direction: taking tools away, which is
// what you want when an agent answers questions about a codebase rather than working on one — a
// documentation assistant, a code-search endpoint, a review bot reading a pull request.
//
// THE POINT IS THAT THE RESTRICTION IS STRUCTURAL. A briefing that says "never edit files" is a
// request the model may ignore; a selection is a list it is never offered, and a withheld tool is
// refused if it guesses the name anyway. Prose asks. This decides.

var workingDir = args.FirstOrDefault() ?? Directory.GetCurrentDirectory();

var resolution = ConfigResolver.Resolve(new AppPaths(), Environment.GetEnvironmentVariables()
    .Cast<System.Collections.DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? ""), useMock: false);

if (!resolution.HasProvider)
{
    // ERRORS, NOT EXCEPTIONS. ConfigResolver catches what the loader throws and reports it this
    // way, so a missing file and a malformed one arrive through one path.
    Console.Error.WriteLine("No provider configured. Run cxagent once to set one up.");
    foreach (var error in resolution.Errors) Console.Error.WriteLine($"  {error}");
    return 1;
}

// THE SELECTION. Read and search, nothing else.
//
// WHY A WHITELIST RATHER THAN `inherited` MINUS THE WRITERS: a bare list names what this agent may
// have, so a tool added to a future version of the library is not silently granted. Subtracting
// would mean revisiting this line every time the built-in set grows — and forgetting to is the
// failure that does not announce itself.
//
// run_shell IS ABSENT FOR A REASON. `cat` reads a file, but `rm` does not, and a shell is the one
// tool whose reach cannot be narrowed by naming it. Leaving it in would make the rest of this list
// decorative — the model would simply route around the missing tools, which is exactly what one did
// on a live drive when web_fetch was withheld and curl was not.
var readOnly = new ToolSelection([Tool.ReadFile, Tool.Glob, Tool.Grep, Tool.TodoWrite]);

using var manager = SessionManager.Create(new AppPaths());

var session = manager.Open(workingDir, resolution,
    new SessionPorts
    {
        Observer = new ConsoleSink(),
        ToolObserver = new NullToolSink(),

        // S2 — this session. SharedServices.ToolSelection would be S1 and cover every session this
        // manager opens; either works here, and the session-level one is the narrower claim.
        ToolSelection = readOnly,
    },

    // SINGLE MODE, because the spawn tool is not in the selection above. Asking for fan-out anyway
    // would be a contradiction the library resolves for you — it falls back to single and says so —
    // but stating it here means the mode and the toolset agree from the first turn rather than
    // after a correction.
    new WorkingMode(AgentMode.Single, EditMode.AlwaysAsk));

// NO PERMISSION GATE IS WIRED, and that is not an omission.
//
// Manager.Create without a buildGate leaves every call ungated — which would be reckless for an
// agent that can write, and is fine for one that cannot. The tools it has read files inside a
// working directory it was given. There is no destructive call to intercept, so there is nothing
// for a human to answer, and a prompt that never fires teaches a user to press Enter without
// reading. THE SELECTION IS DOING THE WORK THE GATE WOULD OTHERWISE DO.
//
// Add a gate the moment you add a tool that changes something. See ToolAgent for one.

Console.WriteLine($"read-only agent · {workingDir}");
Console.WriteLine("offered: read_file, glob, grep, todowrite — no writes, no shell, no sub-agents");
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

/// <summary>Tool activity, ignored — the port is non-nullable, so an embedder that does not care
/// says so explicitly rather than by passing null.</summary>
internal sealed class NullToolSink : IToolObserver
{
    public void ToolsChanged(IReadOnlyList<Job> jobs) { }
    public void ToolUpdated(Job job) { }
    public void ToolProgressed(Job job) { }
    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
    public void ToolOutputAppended(string jobId, string delta) { }
}
