# SpectreAgent

A second front end for [`CxAgent.Core`](../../README.md), in about a hundred lines.

cxagent's own UI is a full TUI — panels, live token gauges, inline permission prompts. This is the
other end of the range: a prompt, streamed text, and one line per tool. The point is that the same
package drives both, because nothing in it assumes a terminal.

```
dotnet run --project cxagent.Core/examples/SpectreAgent -- /path/to/repo
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

## It delegates

`WorkingMode.Default` is fan-out, so this example gets the spawn tool without asking for it:

```
> use a sub-agent to explore the Export folder and tell me what it does
  · llm_agent
  · file
…
76,608 tokens
```

Delegation is capability rather than permission — a child runs under the same gate, in the same
folder, with the same rules. A front end with nowhere to show a child's progress passes
`AgentMode.Single` instead.

## It asks before writing

The permission engine is entirely in `CxAgent.Core` — trust, the working-directory boundary, edit
modes, stored rules, the read-only command list. **The only part this example writes is the
question**, plus the policy that carries the folder:

```
> create a file /tmp/gate-test.txt containing the word hello
  · file

FileWrite /tmp/gate-test.txt
  allow?
> once
  always (/tmp/)
  deny
```

Denied, the file is not written and the model adapts — it noticed `/tmp/` is outside the working
directory and offered a path inside it instead.

`ls cxgpu/Export` does **not** prompt: a read-only verb inside the boundary is allowed silently, and
that decision is the policy's, not this file's.

**Two halves, and one without the other is silent.** `buildGate` gives the decider a way to ask;
`SessionPorts.Policy` gives it the folder and the edit mode to judge against. Omit the policy and
every request is refused with "no session policy" — which this example did at first.

## Two things worth copying

**Announce tools from `ToolsChanged`, not `ToolUpdated`.** The first fires while jobs run; the
second when one finishes. A front end that announces starts from `ToolUpdated` prints nothing at all,
because a finished job is never `Running`. This example got that wrong first.

**Let severity pick the colour.** `Said` carries the session's own notices as a `Message` — markdown
text plus an `Info`/`Warning`/`Error` tone. Core writes no colour tags, so there is nothing to strip;
this example switches on the severity and wraps the text in a Spectre colour of its own choosing.
Do call `EscapeMarkup` on the text first, as `ConsoleSink.Say` does: a path or an error message can
carry a literal bracket, and Spectre would otherwise read it as a tag.
