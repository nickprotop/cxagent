using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Spectre.Console;

// AN AGENT THAT CANNOT CHANGE ANYTHING, and cannot delegate.
//
// ToolAgent shows how to ADD a tool. This shows the other direction: taking tools away, which is
// what you want when an agent answers questions about a codebase rather than working on one — a
// documentation assistant, a code-search endpoint, a review bot reading a pull request.
//
// THE POINT IS THAT THE RESTRICTION IS STRUCTURAL. A briefing that says "never edit files" is a
// request the model may ignore; a selection is a list it is never offered, and a withheld tool is
// refused if it guesses the name anyway. Prose asks. This decides.
//
// EVERY TOOL CALL IS PRINTED, which is the whole reason this example is worth running rather than
// reading. A selection you can only see in source is a claim; one you watch hold for a session is
// evidence. The first version of this file discarded tool events through a null observer — for an
// example about WHICH TOOLS AN AGENT HAS, that hid the only thing worth looking at.

var workingDir = args.FirstOrDefault() ?? Directory.GetCurrentDirectory();

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

AnsiConsole.Write(new Panel(
        $"[grey]{workingDir.EscapeMarkup()}[/]\n\n"
      + $"offered  [green]{string.Join("[/] · [green]", readOnly.Terms)}[/]\n"
      + "withheld [red]write_file · replace_in_file · run_shell · agent · web_fetch · http_request · …[/]")
    .Header("[steelblue] read-only agent [/]")
    .BorderColor(Color.Grey));
AnsiConsole.WriteLine();

var resolution = ConfigResolver.Resolve(new AppPaths(), Environment.GetEnvironmentVariables()
    .Cast<System.Collections.DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? ""), useMock: false);

if (!resolution.HasProvider)
{
    // ERRORS, NOT EXCEPTIONS. ConfigResolver catches what the loader throws and reports it this
    // way, so a missing file and a malformed one arrive through one path.
    AnsiConsole.MarkupLine("[red]No provider configured.[/] Run cxagent once to set one up.");
    foreach (var error in resolution.Errors)
        AnsiConsole.MarkupLine($"  [grey]{error.EscapeMarkup()}[/]");
    return 1;
}

using var manager = SessionManager.Create(new AppPaths());

var session = manager.Open(workingDir, resolution,
    new SessionPorts
    {
        Observer = new ConsoleSink(),
        ToolObserver = new ToolSink(),

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
// SessionManager.Create without a buildGate leaves every call ungated — which would be reckless for
// an agent that can write, and is fine for one that cannot. The tools it has read files inside a
// working directory it was given. There is no destructive call to intercept, so there is nothing
// for a human to answer, and a prompt that never fires teaches a user to press Enter without
// reading. THE SELECTION IS DOING THE WORK THE GATE WOULD OTHERWISE DO.
//
// Add a gate the moment you add a tool that changes something. See ToolAgent for one.

AnsiConsole.MarkupLine("[grey]Ask about the code. Blank line to quit.[/]");
AnsiConsole.WriteLine();

while (true)
{
    // Console.ReadLine RATHER THAN AnsiConsole.Prompt, which throws "Failed to read input in
    // non-interactive mode" the moment stdin is a pipe. An example that dies under
    // `echo "..." | dotnet run` is one nobody can script, test in CI, or paste into an issue.
    AnsiConsole.Markup("[steelblue]>[/] ");
    var prompt = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(prompt)) break;

    // Submit RUNS A COMMAND when the text names one and sends it to the model otherwise. Handled
    // means the command already reported through the observer above, so there is nothing to await
    // and nothing to say — announcing "busy" here would label a command that worked as a refusal.
    var outcome = session.Submit(prompt);
    if (outcome is Session.SubmitOutcome.Handled) continue;

    // THE WHOLE API, in three lines. Submit returns a receipt rather than a task: Started carries the
    // turn, Queued means one was already running, NoAgent means nothing is wired.
    if (outcome is not Session.SubmitOutcome.Started started)
    {
        AnsiConsole.MarkupLine("[yellow]busy[/]");
        continue;
    }

    await started.Turn;
    AnsiConsole.WriteLine();
}

return 0;

/// <summary>Where the session's words go — the minimum an embedder must supply.</summary>
internal sealed class ConsoleSink : ISessionObserver
{
    public void UserTurnAdded(ChatMessageId id, string text) { }
    public void AssistantTurnBegan(ChatMessageId id) { }

    // STREAMED, so Write rather than MarkupLine: the model's text is not markup and a stray
    // bracket in a code identifier would otherwise throw mid-sentence.
    public void AssistantTextAppended(ChatMessageId id, string token) => Console.Write(token);

    public void AssistantReasoningAppended(ChatMessageId id, string text) { }
    public void AssistantTurnEnded(ChatMessageId id) => AnsiConsole.WriteLine();
    public void AssistantLabelled(ChatMessageId id, string header) { }
    // ESCAPED, NOT STRIPPED. Core writes markdown, so there are no colour tags to remove — but a
    // path or an error message can still carry a literal bracket, and Spectre would read that as a
    // tag of its own. The colour comes from severity, which is the only styling Core asks for.
    public void Said(Message message) => AnsiConsole.MarkupLine(message.Severity switch
    {
        Severity.Error => $"[red]{message.Text.EscapeMarkup()}[/]",
        Severity.Warning => $"[yellow]{message.Text.EscapeMarkup()}[/]",
        _ => $"[grey]{message.Text.EscapeMarkup()}[/]",
    });
}

/// <summary>
/// One line per tool call — the selection, visible.
///
/// <para>ANNOUNCED FROM ToolsChanged, NOT ToolUpdated. ToolsChanged carries the live set while tools
/// RUN; ToolUpdated fires when one finishes, and a finished job is never Running — so a front end
/// that announced starts from ToolUpdated prints nothing at all. SpectreAgent's observer carries the
/// same warning, having made the mistake first.</para>
/// </summary>
internal sealed class ToolSink : IToolObserver
{
    private readonly HashSet<string> _announced = [];

    public void ToolsChanged(IReadOnlyList<Job> jobs)
    {
        foreach (var job in jobs)
        {
            if (job.State is not JobState.Running || !_announced.Add(job.Id)) continue;
            AnsiConsole.MarkupLine($"[grey]  · {job.DisplayName.EscapeMarkup()}[/]");
        }
    }

    public void ToolUpdated(Job job) { }
    public void ToolProgressed(Job job) { }
    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
    public void ToolOutputAppended(string jobId, string delta) { }
}
