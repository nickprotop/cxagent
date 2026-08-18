# SpectreAgent

A second front end for [`CxAgent.Core`](../../cxagent.Core/README.md), in about a hundred lines.

cxagent's own UI is a full TUI — panels, live token gauges, inline permission prompts. This is the
other end of the range: a prompt, streamed text, and one line per tool. The point is that the same
package drives both, because nothing in it assumes a terminal.

```
dotnet run --project examples/SpectreAgent -- /path/to/repo
```

It reads the same `config.json` cxagent does, so it runs against whatever provider you already have
configured. A blank line quits.

```
> read cxgpu/Export/UsageView.cs and say how many lines it has
  · file
55 lines.

> 
4,427 tokens
```

## What it shows

**The whole API is four calls.** `SessionManager.Create`, `manager.Open`, `session.Submit`, and
awaiting what `Submit` hands back.

**You supply where text goes.** `ConsoleSink` implements `ISessionObserver` — eight methods, and the
only reason Core never writes to a console itself. `ToolSink` does the same for tool activity.

**`Submit` returns a receipt, not a task.** `Started` carries the turn, `Queued` means one was
already running and this went to the queue, `NoAgent` means nothing is wired. Three outcomes because
they are three different things for a caller to do.

## Two things worth copying

**Announce tools from `ToolsChanged`, not `ToolUpdated`.** The first fires while jobs run; the
second when one finishes. A front end that announces starts from `ToolUpdated` prints nothing at all,
because a finished job is never `Running`. This example got that wrong first.

**Strip Core's markup, or render it.** `Said` carries the session's own notices in a markup dialect —
`[yellow]Stopped.[/]`. A TUI renders it; this example strips it. What you must not do is print it
raw beside model output, which may itself contain brackets.
