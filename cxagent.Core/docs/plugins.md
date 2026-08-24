# Writing a cxagent plugin

*Everything you need to write one.*

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

- [`examples/CalculatorPlugin`](https://github.com/nickprotop/cxagent/tree/master/cxagent.Core/examples/CalculatorPlugin) — managed, ~110 lines
- [`examples/CalculatorAbiPlugin`](https://github.com/nickprotop/cxagent/tree/master/cxagent.Core/examples/CalculatorAbiPlugin) — C, one file

Read them side by side. Nothing differs but the boundary, so the diff between them *is* what the
boundary costs.

The [other examples](https://github.com/nickprotop/cxagent/tree/master/cxagent.Core/examples) cover
the rest: a second front end, injecting tools, and narrowing what an agent may do.

## The project

A managed plugin is an ordinary `net10.0` library — no `RuntimeIdentifier`, so one build runs
everywhere cxagent does:

```xml
<ItemGroup>
  <!-- Private="false": the host process already has CxAgent.Core loaded, and the plugin resolves it
       from there at run time. Without this the DLL ships beside every copy of your plugin for no
       reason, and a version that disagrees with the host's is a bug waiting to happen. -->
  <ProjectReference Include="path/to/cxagent.Core.csproj" Private="false" />
</ItemGroup>

<ItemGroup>
  <None Update="myplugin.plugin.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

A consumer outside this repo writes `<PackageReference Include="CxAgent.Core" Version="…" />` with
the same `Private="false"`.

What ships is two files: your DLL and its sidecar.

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

**Per-call gating is `"gated": true` in your manifest.** A gated tool asks before each call, and the
prompt offers "Always" like any other. The stored rule names your plugin as well as your tool
(`plugin calculator tool calc_add`), so it cannot outlive you: uninstall your plugin, install a
different one declaring the same tool name, and the newcomer starts from no permission rather than
inheriting a grant the user gave you.

Whether to grant it standing is the user's call — they already approved your binary at load, against
a hash of its contents, which is the decision that actually determines whether your code runs.

**Mark your sharp edges with `"alwaysAskable": false`.** That tool asks every call and never offers
"Always". Use it where a standing grant would be wrong even for a plugin the user trusts:

```json
{ "name": "rename_symbol", "gated": true, "alwaysAskable": false }
```

Your `definition` lookup is a read, and a user should be able to stop being asked. Your `rename`
rewrites files across a repository. One flag for the whole plugin would force those to share an
answer; nothing cxagent can see — a tool name, a schema — tells them apart. You know which is which.

Omit the field and it defaults to true, matching every other permission in cxagent.

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
- `Settings` — your own config block, verbatim. See below.
- `Logger` — reaches the user's transcript.
- `Lifetime` — cancelled when the plugin stops. Not a per-call token: use it for a long-lived
  backend, and a call's own token for the call.
- `RegisterChildProcess(pid)`.

**You are not handed the transcript, the model, or the permission store.** That is deliberate and
permanent.

## Settings

`context.Settings` is your plugin's own `"settings"` block from config.json, handed over exactly as
the user wrote it. cxagent checks that it parses and nothing more — it has no idea what your plugin
expects.

```json
"plugins": {
  "calculator": { "file": "calculator.dll", "settings": { "precision": 4 } }
}
```

```csharp
private static int ReadPrecision(JsonElement settings)
{
    if (settings.ValueKind != JsonValueKind.Object ||
        !settings.TryGetProperty("precision", out var value) ||
        value.ValueKind != JsonValueKind.Number ||
        !value.TryGetInt32(out var precision))
        return DefaultPrecision;

    return Math.Clamp(precision, 0, 15);
}
```

**Read your behaviour from here rather than hardcoding it.** This is what lets one binary serve
several configurations: the C# language-server plugin that ships with cxagent drives both `csharp-ls`
and OmniSharp from `settings.server` and `settings.args`, with no branch anywhere on which one it is
talking to.

**Missing is not an error, and neither is a typo.** A plugin configured with no settings block gets
an empty object, so every read needs a default — and a default that works is the difference between
a plugin someone can try and one they must configure before it will start. `"precision": "four"`
is a mistake, not an attack; produce a working plugin at the default rather than a stack trace at
load.

Both calculator examples read `settings.precision` this way, and both fall back cleanly.

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

- [`IPlugin.cs`](https://github.com/nickprotop/cxagent/tree/master/cxagent.Core/Core/Plugins/IPlugin.cs) — the managed contract, documented per method
- [`Abi/cxagent_plugin.h`](https://github.com/nickprotop/cxagent/tree/master/cxagent.Core/Core/Plugins/Abi/cxagent_plugin.h) — the six C functions, with the ownership rules
- [`Abi/README.md`](https://github.com/nickprotop/cxagent/tree/master/cxagent.Core/Core/Plugins/Abi/README.md) — the JSON envelopes crossing the ABI boundary

Hosting a plugin rather than writing one:

- [`docs/api.md`](api.md#plugins) — `LoadPlugin`, the registry, the loader, child-process reaping
- [`docs/tools.md`](tools.md) — the other kind of tool: compiled into your app rather than loaded
- [the package README](../README.md#tools-that-arrive-at-run-time) — where plugins sit among everything else
