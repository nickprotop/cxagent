# Writing a cxagent plugin

A plugin adds tools to a session. It declares what it offers, cxagent asks the user once whether to
trust it, and from then on the model can call its tools like any built-in.

Two kinds, and the choice is made for you by your language:

| | **managed** | **ABI** |
| --- | --- | --- |
| written in | C# | C, Rust, Go, C++ — anything with a C ABI |
| runs in | cxagent's own process | a separate host process |
| a crash | takes cxagent with it | fails the call, session survives |
| you implement | `IPlugin` — four methods | six `extern "C"` functions |

**Write managed if you are writing C#.** An ABI plugin in .NET needs NativeAOT, which strips the
reflection `System.Text.Json` depends on, so every payload then needs a hand-written `JsonTypeInfo`.
It is measurably more code to reach a place the host would have loaded directly.

Two working examples, the same calculator twice:

- [`examples/CalculatorPlugin`](../../examples/CalculatorPlugin) — managed, ~110 lines
- [`examples/CalculatorAbiPlugin`](../../examples/CalculatorAbiPlugin) — C, one file

Read them side by side. Nothing differs but the boundary, so the diff between them *is* what the
boundary costs.

## The lifecycle

Four calls, in this order, whichever kind you write:

```
Load(context) -> manifest    once, before anything else. Say what you offer.
Start()                      spawn processes, open connections, build indexes.
Invoke(tool, call) -> result once per tool call, any number of times.
Stop()                       close what Start opened. Must tolerate an already-dead backend.
```

`Start` is not optional and not lazy. The tool list is fixed once a request begins, so a backend that
comes up on first use is a tool that fails its first call and works on its second.

## The sidecar

Your plugin ships two files: the binary, and `<name>.plugin.json` beside it. cxagent reads the
sidecar *before* loading the binary — so a user can see what a plugin claims without running it —
and refuses the load if it disagrees with what `Load` returns.

```json
{
  "name": "calculator",
  "version": "1.0.0",
  "spawns": false,
  "instructions": "Shown to the model, once, describing how to use this plugin's tools together.",
  "tools": [
    {
      "name": "calc_add",
      "description": "Adds two numbers.",
      "inputSchema": { "type": "object", "properties": { "a": { "type": "number" } }, "required": ["a"] },
      "gated": true
    }
  ]
}
```

The simplest way to keep the two in step is to parse the sidecar in `Load` and return that — one
JSON, true by construction. Both examples do this.

An ABI plugin's manifest additionally carries `"abiVersion": 1`. Omit it and it reads as 0, and the
load is refused for an unsupported version the file never mentions.

**Read it from beside your own assembly, not `AppContext.BaseDirectory`.** That property is the
running process's folder — where cxagent lives, not where your plugin was found. They coincide only
when a plugin sits in the app's output directory, which is what a test does and production never
does.

## Naming your tools

A tool name is claimed session-wide. A plugin cannot take a built-in's name, and two plugins cannot
share one — the second to load is refused entirely, and the user's only remedy is editing a plugin
they did not write.

So name tools for what makes them **yours**. `lsp_definition` is a name every language-server plugin
has equal claim to, which makes a C# one and a Rust one mutually exclusive. `csharp_definition` and
`rust_definition` both fit, and the model picks between them by reading the names.

Prefer the specific name even when yours is the only such plugin today. The collision arrives when
someone installs the second one, and by then your name is in their config.

## Permission

**cxagent asks once, at load, whether to trust the binary.** That prompt names the plugin and shows
a content hash covering its whole load set — change any byte and the user is asked again. This is
the only boundary cxagent can enforce on your behalf, and nothing in config can pre-approve it.

**Per-call gating is `"gated": true` in your manifest.** A gated tool asks on *every* call, with no
"always allow" — deliberately, because a stored rule would be a standing grant to code cxagent did
not write.

Past that, permission is yours. cxagent cannot know which of your operations are dangerous; if your
plugin can delete things, gate it yourself before doing them.

The calculator examples gate addition and not multiplication. That is absurd, and it is the point: a
gate on something genuinely dangerous teaches you what the danger was, while a gate on `2 + 2` can
only teach you the mechanism.

## Returning a result

```csharp
new JobResult
{
    Success = true,
    Output =
    {
        ["content"] = "5",   // what the MODEL reads
        ["answer"] = 5.0,    // structured, for anything else
    },
}
```

`content` is not optional in practice. A result carrying only structured keys reaches the model as an
empty string — not "no results", *nothing* — and the model explains the silence rather than reporting
it. Found the hard way: a language server plugin that was running and answering correctly the whole
time, while the model reported it as unavailable.

Say so when you find nothing. `"No definition found at that position."` is something the model can
act on; a blank string is something it has to guess about.

## Spawning processes

If your plugin starts a process, say `"spawns": true` and register it the moment it exists:

```csharp
context.RegisterChildProcess(process.Id);
```

Register **before** any handshake with it. cxagent records the pid and reaps it at the next startup
if this session dies without reaching `Stop` — but only for what it was told about. A process that
starts and then fails to initialise still needs reaping, and registering after the handshake misses
exactly that case.

## What a plugin is handed

`IPluginContext` gives you:

- `WorkingDirectory` — the folder being worked in. Root yourself here.
- `Settings` — your own config block, verbatim. Read your server path, flags and options from it
  rather than hardcoding them; that is what lets one binary serve two configurations.
- `Logger` — reaches the user's transcript.
- `Lifetime` — cancelled when the plugin stops. Not a per-call token: use it for a long-lived
  backend, and a call's own token for the call.
- `RegisterChildProcess(pid)`.

**You are not handed the transcript, the model, or the permission store.** That is deliberate and
permanent.

## Configuring one

```json
"pluginPaths": ["~/.config/cxagent/plugins"],
"plugins": {
  "calculator": {
    "file": "calculator.dll",
    "enabled": true,
    "settings": { "precision": 4 }
  }
}
```

Searched in `pluginPaths` order, then `<project>/.cxagent/plugins`, then `<config>/plugins` — so a
project's copy wins over a globally installed one.

`enabled: false` means no process, no tools, no prompt, nothing. It is configuration, not permission:
loading still asks, every time. `/plugin load <name> --once` overrides `enabled: false` for one
session and still asks.

A note on where to put them: some language servers crawl the workspace for projects and will happily
index a plugin binary sitting in `.cxagent/plugins`. The global folder avoids that.

## See also

- [`IPlugin.cs`](IPlugin.cs) — the managed contract, documented per method
- [`Abi/cxagent_plugin.h`](Abi/cxagent_plugin.h) — the six C functions, with the ownership rules
- [`Abi/README.md`](Abi/README.md) — the JSON envelopes crossing the ABI boundary
- [`PLUGINS.md`](../../../PLUGINS.md) — the design: why the boundaries are where they are
