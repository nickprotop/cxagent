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

`SessionPorts.Observer` is an `ISessionObserver`: where assistant text, tool activity and the
session's own notices arrive. `BufferedChatSink` is a real non-UI implementation if you only need to
collect output.

Nothing in this package writes to a console, opens a window, or ends a process.

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

## What is not here

The terminal. `cxagent` itself supplies the window, the message loop, and the four commands that need
one (`/help`, `/exit`, `/mcp`, and the confirmation half of `/stats`). Everything else — `/model`,
`/mode`, `/sessions`, `/skills`, `/diff`, `/compress`, `/clear`, `/agents`, `/stats` — is in this
package and works headlessly.

## License

MIT. See [LICENSE](https://github.com/nickprotop/cxagent/blob/master/LICENSE).
