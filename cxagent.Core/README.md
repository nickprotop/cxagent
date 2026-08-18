# CxAgent.Core

The sessions, agents and turn loop behind [cxagent](https://github.com/nickprotop/cxagent) — usable
without a terminal.

cxagent is a TUI coding agent. This package is everything underneath it: a conversation, the agent
running it, tool execution, sub-agent delegation, permissions, MCP, and the stores that let a session
be resumed. No UI dependency — you supply where text goes.

## Install

```
dotnet add package CxAgent.Core
```

## An app is four calls

```csharp
using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;

var manager = SessionManager.Create(new AppPaths(configDir));

var session = manager.Open(
    workingDirectory,
    ResolvedConfig.ForTesting(provider),          // or resolve from config.json
    new SessionPorts { Observer = sink, Tools = jobs });

if (session.Submit("summarise this folder") is Session.SubmitOutcome.Started started)
    await started.Turn;
```

`Submit` returns a receipt rather than a task, because there are three outcomes and they are three
different things for a caller to do:

| Outcome | Meaning |
|---|---|
| `Started(Task Turn)` | a turn began — await it, or attach a continuation |
| `Queued` | a turn was already running; this was queued for its next tool barrier |
| `NoAgent` | nothing is wired — leave the text where it is |

## You supply the observer

`SessionPorts.Observer` is an `ISessionObserver` — where assistant text, tool activity and the
session's own notices arrive. Nothing in this package writes to a console, opens a window, or ends a
process, and this is why.

A complete one, in the shape a console front end wants:

```csharp
internal sealed class ConsoleSink : ISessionObserver
{
    public void UserTurnAdded(ChatMessageId id, string text) { }   // already on screen
    public void AssistantTurnBegan(ChatMessageId id) { }

    // Written raw: this is model output, and a stray bracket in it is not a colour tag.
    public void AssistantTextAppended(ChatMessageId id, string token) => Console.Write(token);

    public void AssistantReasoningAppended(ChatMessageId id, string text) { }   // hide it, or don't
    public void AssistantTurnEnded(ChatMessageId id) => Console.WriteLine();
    public void AssistantLabelled(ChatMessageId id, string header) { }

    // The session's OWN notices — a mode change, a model switch, "Stopped." — in Core's markup
    // dialect. Render the tags, or strip them; do not print them raw beside model output.
    public void Said(string message) => Console.WriteLine(Strip(message));
    public void Failed(string message) => Console.Error.WriteLine(message);

    private static string Strip(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[^\]]*\]", "");
}
```

And the tool half, which is optional — pass a no-op if you do not want job rows:

```csharp
internal sealed class ToolSink : IToolObserver
{
    private readonly HashSet<string> _announced = [];

    // ANNOUNCE FROM HERE, not from ToolUpdated. This fires while jobs RUN; ToolUpdated fires when
    // one FINISHES, so announcing starts from there prints nothing at all — a finished job is never
    // Running. The Spectre example got this wrong first.
    public void ToolsChanged(IReadOnlyList<Job> jobs)
    {
        foreach (var job in jobs)
            if (job.State is JobState.Running && _announced.Add(job.Id))
                Console.WriteLine($"  · {job.PluginType}");
    }

    public void ToolUpdated(Job job) { }
    public void ToolProgressed(Job job) { }
    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
    public void ToolOutputAppended(string jobId, string delta) { }
}
```

`BufferedChatSink` and `BufferedJobPanel` ship with the package if you only need to collect output —
they are what the tests use.

## Steering a running turn

Text typed while a turn runs is delivered at the turn's **next tool barrier**, where the model can
still act on it:

```csharp
session.Pending   += (whole, added) => /* draw a "queued" block */;
session.Drained   += text           => /* it went in — take the block down */;
session.Cancelled += text           => /* Escape: put it back in the composer */;

session.Steer("actually, only the Export folder");
```

`session.CancelTurn()` stops the turn, says so through the observer, and hands anything queued back
through `Cancelled` — it does not decide where that text goes.

## Permissions

`SharedServices.Gate` is null by default, which means **no gating at all** — an ordinary headless
arrangement, but a deliberate choice rather than an inherited one. Supply a gate to have shell
commands, file writes and network calls asked about; the policy layers trust (per folder), an edit
mode, stored rules, and a working-directory boundary.

## Sub-agents

A session in fan-out mode can delegate. Children are agents without a session — their own context,
their own log directory, their own token budget — and they report back as a tool result.

## Reading what a session is doing

Everything a front end draws comes from the observer plus a few properties:

```csharp
session.TokensUpdated           += (_, total) => …;   // after each provider call
session.ContextUsedUpdated      += (_, used)  => …;   // how full the window is
session.ContextCompressed       += (_, e)     => …;   // (Before, After)
session.TurnCompleted           += (_, turns) => …;
session.Changed                 += kind       => …;   // Mode, Model, Resumed, TurnCancelled…

session.Ledger        // spend, by model and by agent, with cache rates
session.OwnSpend      // this agent alone, excluding children
session.IsBusy        // a turn is running
session.Mode          // delegation and edit mode
```

Events attach to the host that exists when you subscribe, so subscribe after opening the session.

## Commands

A session services the commands cxagent exposes, and they work headlessly:

```csharp
session.SetMode("edits auto");      // or SetMode(WorkingMode)
session.UseFromInput("openrouter"); // /model — parses what a user typed
session.ListSessions("all");
session.SayUsage("30");             // /stats
session.ClearContext();
session.CompressNow(ct);
```

Each returns a `CommandStatus` — `Reported` (it said something), `Changed` (the session moved),
`Refused` (it could not run now, and said why), or `Unknown` (nothing services it). `.Handled()`
collapses that to a bool if you are routing input.

`SessionManager.Commands` is the registry those are seeded into; a front end registers its own on top
and the last registration wins.

## A worked example

[`examples/SpectreAgent`](examples/SpectreAgent) is
a second front end in about a hundred lines — a prompt, streamed output, and a line per tool — built
on [Spectre.Console](https://spectreconsole.net/) rather than the TUI cxagent itself uses. It reads
the same `config.json`, so it runs against whatever provider is already configured:

```
dotnet run --project cxagent.Core/examples/SpectreAgent -- /path/to/repo
```

## What is not here

The terminal. `cxagent` itself supplies the window, the message loop, and the four commands that need
one (`/help`, `/exit`, `/mcp`, and the confirmation half of `/stats`). Everything else — `/model`,
`/mode`, `/sessions`, `/skills`, `/diff`, `/compress`, `/clear`, `/agents`, `/stats` — is in this
package and works headlessly.

## Reference

**[Full API reference →](docs/api.md)** — every public member, grouped by the question it answers:
running a turn, the steer queue, watching progress, commands, what you supply, permissions and
configuration.

## License

MIT. See [LICENSE](https://github.com/nickprotop/cxagent/blob/master/LICENSE).
