# Injecting your own tools

The agent gets read, write, edit, glob, grep, shell, http and web-fetch without you registering
anything. This is about the other kind — a tool **you** write, that does something this library could
not have anticipated.

```csharp
new SessionPorts
{
    Observer = new ConsoleSink(),
    ToolObserver = new NullToolSink(),
    Policy = policy,
    Tools = [new DeployTool(), new QueryWarehouseTool()],
}
```

That is the whole wiring. Everything below is about the one method that makes it safe.

---

## The interface

```csharp
public interface IAgentTool
{
    ToolDefinition Definition { get; }                  // what the model sees
    bool OfferToSubAgents => true;                      // who is offered it
    PermissionRequest? Gate(JobParameters call);        // whether THIS call needs a human
    Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct);
}
```

`ToolDefinition`, `JobParameters`, `IJobContext` and `JobResult` are the existing executor contracts, so
your tool gets the working directory, progress reporting and cancellation for free.

---

## The two gates

This is the part worth reading twice, because the whole design turns on it.

| | Asked | Answers |
|---|---|---|
| **Gate 1** | by the engine, before your tool runs | may this tool run in this folder at all |
| **Gate 2** | your `Gate()`, **every call, forever** | does *this call's arguments* need a human |

Gate 1 is a `PermissionKind.Tool` question — "use the deploy tool in this folder" — and answering
**Always** stores a rule per folder, per tool. After that it stops asking.

**Being trusted is permission to USE the tool. It is never an exemption from the tool's own checks.**
Collapsing the two would mean one "always allow" disarmed every future call, which is the failure
this codebase keeps finding in other forms: a check that examines part of a request and lets the rest
through unexamined.

You do not wire gate 1. `SessionFactory` wraps every injected tool in `GatedAgentTool` on the way
through, because a bare `IAgentTool` reaching an agent is not a compile error and would run ungated.

### Nothing caches the answer

Gate 1 is asked on **every call**, and goes silent only once a stored rule matches.

That is deliberate. `IPermissionGate.RequestAsync` returns a bare `bool`, so "Allow once" and "Always
allow" are indistinguishable to anything downstream of the prompt — a wrapper that cached the yes
would be caching an answer it cannot read. The first version of this did exactly that, and one
"once" silently covered four files. Remembering belongs to the rules store or nowhere.

---

## What `Gate` should return

The returned request's `AlwaysRule` decides the granularity of "Always". This is the most consequential
line in your tool.

| `AlwaysRule` | Asked | Right when |
|---|---|---|
| `null` | every time | the arguments are free text — `notify @ceo` and `notify #eng` differ in a way no rule captures |
| `"deploy env=dev"` | once per environment | the scope you want to grant is a *property* of the call |
| `"deploy*"` | once, ever | rarely. This hands over production on the strength of a yes about a dev box |

Returning `null` from `Gate` itself (not a null `AlwaysRule`) means **no human needed** — correct for
a pure function like a calculator, and a claim about your tool that nothing else will second-guess.

```csharp
public PermissionRequest? Gate(JobParameters call)
{
    var env = call.Get("env", "");
    return new PermissionRequest(
        PermissionKind.Tool,
        $"deploy to {env}",              // what the human reads
        AlwaysRule: $"deploy env={env}"); // exactly what "Always" would store
}
```

`Gate` runs before every call and must not do I/O. It inspects the arguments and says what permission
they imply, nothing more.

### Resolve paths before you judge them

If your tool takes a path, resolve it in `Gate` before building the request:

```csharp
var resolved = Path.GetFullPath(Path.Combine(_workingDirectory, call.Get("path", "")));
```

`../../etc/passwd` is inside the working directory only as a string. The policy compares real paths,
and handing it an unresolved one is the recurring shape of bug here.

### Do not decide "inside or outside" yourself

If your tool reads files, return a `FileRead` request and let `PermissionPolicy` answer it. It is
tempting to return `null` for anything under the working directory on the grounds that reads there
are free — but the free pass is `AllowsSilentWrites`, which requires the folder to be **trusted** as
well as in-boundary. A tool that returns null for anything under the cwd skips the prompt in exactly
the case the prompt exists for.

The policy's own comment is the reason: a second copy of "trusted and in-boundary" *"would be the
copy that drifts."*

---

## Returning a result

```csharp
return new JobResult
{
    Success = true,
    Output = { ["content"] = "what happened" },
};
```

`Output["content"]` is what the transcript shows **and** what the model is told. If those should
differ, add `"summary"`:

```csharp
Output =
{
    ["content"] = renderedMarkup,     // for the transcript
    ["summary"] = "4 rows, shown above",  // for the model
},
```

cxagent's own `show_diff` needs this: its content is terminal markup for a person to look at, and
handing the model a blob of colour tags costs a turn of it describing them.

**Never throw.** An exception unwinding the tool loop leaves tool calls with no matching results — an
orphan the provider rejects with a 400 that no recovery path matches, and every later prompt in the
session then fails. Return a failed `JobResult` instead; the error becomes the tool result.

---

## Sub-agents

Children inherit your tools by default. A child edits files exactly as its parent does, so it needs
the same tools.

```csharp
public bool OfferToSubAgents => false;
```

Return false when your tool draws for a **person**. A child's tool rows go to a buffer that is never
displayed — it exists to keep a child's rows out of the parent's transcript — so a rendering tool
would do the work, report success, and have its output discarded. The model is then told its showing
worked when nobody saw anything.

A tool never OFFERED to a child — because `OfferToSubAgents` is false, or because the child was
built without it — does not exist for that child at all, so calling it gets the ordinary "no such
tool". That is the same mechanism making "no sub-agents of sub-agents" structural rather than a rule
an agent is asked to follow.

A tool this build ships but a **selection** withheld is a different answer: `not available`. The
distinction is for the model — "no such tool" means the name is wrong and it should try another,
"not available" means stop asking. See [tool selection](api.md#tool-selection).

---

## Names, and what you cannot shadow

Injected tools are dispatched **after** every built-in, so naming a tool `read_file` does not
override the built-in — it makes your tool unreachable. Pick names that are unmistakably yours.

Within your own set, a duplicate name means the last one registered wins.

---

## A complete tool

```csharp
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Jobs;

public sealed class DeployTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "deploy",
        "Deploy the current build to an environment.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { env = new { type = "string" } },
            required = new[] { "env" },
        }));

    // Asked once per environment: "always" for dev does not answer for prod.
    public PermissionRequest? Gate(JobParameters call)
    {
        var env = call.Get("env", "");
        return new PermissionRequest(PermissionKind.Tool, $"deploy to {env}",
            AlwaysRule: $"deploy env={env}");
    }

    public async Task<JobResult> ExecuteAsync(
        JobParameters call, IJobContext context, CancellationToken ct)
    {
        var env = call.Get("env", "");

        try
        {
            await MyDeployer.RunAsync(env, ct);
            return new JobResult { Success = true, Output = { ["content"] = $"deployed to {env}" } };
        }
        catch (Exception ex)
        {
            // The error becomes the RESULT, never an exception out of ExecuteAsync.
            return new JobResult { Success = false, ExitCode = -1, ErrorMessage = ex.Message };
        }
    }
}
```

---

## A runnable example

[`examples/ToolAgent`](../examples/ToolAgent) is a small console front end with three tools, chosen so
that each demonstrates a different answer from `Gate`:

| Tool | `Gate` returns | Behaviour |
|---|---|---|
| `calc` | `null` | never asks — it adds two numbers and touches nothing |
| `deploy` | `AlwaysRule = "deploy env=dev"` | asked once **per environment** |
| `notify` | `AlwaysRule = null` | asked **every time**; no "Always" button is offered |

```bash
dotnet run --project cxagent.Core/examples/ToolAgent
```

Answer **always** to `deploy env=dev`, then ask for a deploy to `prod`. It asks again — the scope of
what you agreed to was decided by the rule the tool returned.

---

## Checklist

- [ ] `Definition` has a name no built-in uses
- [ ] `Gate` resolves any path before building the request
- [ ] `AlwaysRule` is exactly what you would want "Always" to remember — or `null` if nothing honest fits
- [ ] `ExecuteAsync` returns a failed `JobResult` rather than throwing
- [ ] `Output["content"]` carries what should appear in the transcript
- [ ] `OfferToSubAgents` is false if the tool needs a screen

---

- [API reference →](api.md)
- [Permissions →](../README.md#permissions)
