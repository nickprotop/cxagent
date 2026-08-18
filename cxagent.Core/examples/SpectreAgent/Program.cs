using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Spectre.Console;

// A SECOND FRONT END, in about a hundred lines.
//
// cxagent's own UI is a full TUI. This is the other end of the range: a prompt, a spinner, and
// streamed text — enough to show that CxAgent.Core does not assume a terminal, a window, or a
// message loop. Everything here is the observer contract and four calls.

var workingDir = args.FirstOrDefault() ?? Directory.GetCurrentDirectory();
var configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
    ? Path.Combine(x, "cxagent")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "cxagent");

AnsiConsole.Write(new FigletText("cxagent").Color(Color.SteelBlue));
AnsiConsole.MarkupLine($"[grey]{workingDir.EscapeMarkup()}[/]");
AnsiConsole.WriteLine();

// THE SAME config.json cxagent ITSELF READS. A consumer with its own configuration builds a
// ResolvedConfig directly instead — this reuses the file so the example runs against whatever
// provider is already set up, without a second place to configure a model.
var resolution = ConfigResolver.Resolve(new AppPaths(configDir), Environment.GetEnvironmentVariables()
    .Cast<System.Collections.DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? ""), useMock: false);

if (!resolution.HasProvider)
{
    AnsiConsole.MarkupLine("[red]No provider configured.[/] Run cxagent once to set one up, or point");
    AnsiConsole.MarkupLine("[red]XDG_CONFIG_HOME at a directory containing config.json.[/]");
    foreach (var e in resolution.Errors) AnsiConsole.MarkupLine($"[red]  {e.EscapeMarkup()}[/]");
    return 1;
}

using var manager = SessionManager.Create(new AppPaths(configDir));

var console = new ConsoleSink();
// NO MODE PASSED, so this gets WorkingMode.Default — fan-out with always-ask edits. Delegation is
// on because it is capability rather than permission: a child runs under the same gate, in the same
// folder. A front end with nowhere to show a child would pass AgentMode.Single instead.
var session = manager.Open(workingDir, resolution,
    new SessionPorts { Observer = console, Tools = new ToolSink() });

AnsiConsole.MarkupLine($"[grey]model:[/] {session.InstanceName.EscapeMarkup()} · [grey]blank line to quit[/]");
AnsiConsole.WriteLine();

while (true)
{
    // READ RATHER THAN Ask, because AnsiConsole.Ask THROWS when stdin is not a terminal — piping a
    // prompt in is the obvious way to try an example, and crashing on it is a poor first impression.
    AnsiConsole.Markup("[steelblue]>[/] ");
    var prompt = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(prompt)) break;

    // THE WHOLE API, in three lines. Submit returns a receipt rather than a task: Started carries the
    // turn, Queued means one was already running, NoAgent means nothing is wired.
    if (session.Submit(prompt) is not Session.SubmitOutcome.Started started)
    {
        AnsiConsole.MarkupLine("[yellow]busy[/]");
        continue;
    }

    await started.Turn;
    AnsiConsole.WriteLine();
}

if (session.Ledger is { TotalTokens: > 0 } ledger)
    AnsiConsole.MarkupLine($"[grey]{ledger.TotalTokens:N0} tokens[/]");

return 0;

/// <summary>
/// Where the session's words go. This is the ONLY thing a front end must supply, and it is why Core
/// never writes to a console itself: a log writer, a web socket and this all implement the same
/// eight methods differently.
/// </summary>
internal sealed class ConsoleSink : ISessionObserver
{
    private bool _streaming;

    public void UserTurnAdded(ChatMessageId id, string text) { }   // already on screen — the user typed it

    public void AssistantTurnBegan(ChatMessageId id) => _streaming = false;

    public void AssistantTextAppended(ChatMessageId id, string token)
    {
        // Written raw rather than through markup: this is model output, and a stray bracket in it
        // must not be read as a colour tag.
        if (!_streaming) { _streaming = true; }
        AnsiConsole.Write(new Text(token));
    }

    public void AssistantReasoningAppended(ChatMessageId id, string text) { }   // hidden here

    public void AssistantTurnEnded(ChatMessageId id)
    {
        if (_streaming) AnsiConsole.WriteLine();
        _streaming = false;
    }

    public void AssistantLabelled(ChatMessageId id, string header) { }

    /// <summary>The session's own notices — a mode change, a model switch, "Stopped.".</summary>
    public void Said(string message) => AnsiConsole.MarkupLine($"[grey]{Strip(message)}[/]");

    public void Failed(string message) => AnsiConsole.MarkupLine($"[red]{message.EscapeMarkup()}[/]");

    /// <summary>Core speaks in its own markup dialect; this front end renders plain text instead.</summary>
    private static string Strip(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[^\]]*\]", "").EscapeMarkup();
}

/// <summary>
/// What the agent is doing with its tools. cxagent draws a live panel per job; this prints one line
/// when a tool starts and lets the result speak for itself.
/// </summary>
internal sealed class ToolSink : IToolObserver
{
    private readonly HashSet<string> _announced = [];

    /// <summary>
    /// Called with the live set while tools RUN. <see cref="ToolUpdated"/> fires when one finishes,
    /// which is the distinction worth knowing: a front end that announced starts from ToolUpdated —
    /// as this one first did — prints nothing at all, because a finished job is never Running.
    /// </summary>
    public void ToolsChanged(IReadOnlyList<Job> jobs)
    {
        foreach (var job in jobs)
        {
            if (job.State is not JobState.Running || !_announced.Add(job.Id)) continue;
            AnsiConsole.MarkupLine($"[grey]  · {job.PluginType.EscapeMarkup()}[/]");
        }
    }

    public void ToolUpdated(Job job) { }

    public void ToolProgressed(Job job) { }
    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
    public void ToolOutputAppended(string jobId, string delta) { }
}
