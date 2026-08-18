# ToolAgent — injecting your own tools

Three tools this example owns, offered to the model beside cxagent's built-ins. The interesting part
is one line in each: what `Gate` returns.

```bash
dotnet run --project cxagent.Core/examples/ToolAgent
```

## The two gates

They are different questions, and conflating them is the mistake the design exists to prevent.

**Gate 1 — may this tool run in this folder at all?** Asked by the permission engine, and persisted
as a `Tool` rule in `permissions.json` **only if you answer "Always"**.

Answer "once" and it asks again on the next call — which is what "once" means. Nothing caches the
answer between calls: `IPermissionGate.RequestAsync` returns a bare bool, so once and always look
identical to the tool, and only the rules store can tell them apart. Remembering happens there or
not at all.

**Gate 2 — does THIS call need a human?** Your tool's own `Gate` method, on every call, for the life
of the session.

Answering gate 1 with "always" is permission to *use* the tool. It never exempts a call from gate 2.
A tool trusted in a folder still runs its own checks every single time — otherwise one "always"
would disarm every future call, which is a check that examines part of a request and lets the rest
through unexamined.

## What each tool demonstrates

| Tool | `Gate` returns | Behaviour |
|---|---|---|
| `calc` | `null` | never asks — it adds two numbers and touches nothing |
| `deploy` | request with `AlwaysRule = "deploy env=dev"` | asked once **per environment** |
| `notify` | request with `AlwaysRule = null` | asked **every time**; no "always" is offered |

Try it: answer **always** to `deploy env=dev`, then ask for a deploy to `prod`. It asks again. The
scope of what you agreed to was decided by the rule the tool returned — not by the gate, which can
only honour what it was given.

`notify` never offers "always" at all. Its arguments are free text, and no rule string distinguishes
`notify #eng` from `notify @ceo`; a button that promised to remember one would promise something the
rule system cannot keep.

## Wiring

```csharp
new SessionPorts
{
    Observer = new ConsoleSink(),
    ToolObserver = new NullToolSink(),
    Policy = policy,
    Tools = [new Tools.Calc(), new Tools.Deploy(), new Tools.Notify()],
}
```

`SessionFactory` wraps each one in `GatedAgentTool` on the way through, so nothing here has to
remember to. A bare `IAgentTool` reaching an agent is not a compile error and would run with no gate
at all — which is why the wrapping happens in the one place that has both the gate and the session's
policy, rather than being left to each embedder.

## Sub-agents

Children inherit injected tools by default — a child edits files exactly as its parent does, so it
needs the same tools. That is unlike `spawn` and `ask_user`, which are withheld because a child has
no spawner and no user.

A tool that draws for a PERSON is the exception, and says so itself:

```csharp
public bool OfferToSubAgents => false;
```

A child's tool rows go to a buffer that is never displayed — it exists to keep a child's rows out of
the parent's transcript — so a rendering tool would do the work, report success, and have its output
discarded. The model would be told its showing worked when nobody saw anything.

A withheld tool is one the child was **never given**: calling it gets the ordinary "no such tool",
the same mechanism that makes "no sub-agents of sub-agents" structural rather than a rule an agent is
asked to follow.

---

**[Injecting your own tools →](../../docs/tools.md)** — the full guide: what `Gate` should return,
how `AlwaysRule` decides granularity, and the checklist for a tool of your own.
