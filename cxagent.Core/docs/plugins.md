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
| you implement | `IPlugin` — four methods, plus `IPluginGateSource` if you gate per call | seven `extern "C"` functions |

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

**Reference `CxAgent.Plugins.Abstractions`, not `CxAgent.Core`.** It carries the contract —
`IPlugin`, `IPluginContext`, `PluginManifest`, `IAgentTool` and the job types a tool exchanges — and
nothing else, so your project does not compile against the agent runtime to implement seven
interfaces. Its package version IS the contract number: `2.0.0` is `"pluginContract": 2`.

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

An ABI plugin carries `pluginContract` in its describe JSON as well as in its sidecar — the
handshake export answers before any JSON is parsed, and the body lets a host check a manifest it
already holds. Omit it and it reads as 0, and the
load is refused for an unsupported version the file never mentions.

**Read it from beside your own assembly, not `AppContext.BaseDirectory`.** That property is the
running process's folder — where cxagent lives, not where your plugin was found. They coincide only
when a plugin sits in the app's output directory, which is what a test does and production never
does.

## Adding to the model's instructions

A plugin's `instructions` is a block of text that joins the system prompt while the plugin is
loaded — for what the individual tool descriptions cannot say, because it is true of the set rather
than of one call:

```json
{
  "name": "csharp-lsp",
  "instructions": "Positions are 1-based — line 1 is the first line, character 1 the first column.",
  "tools": [ ... ]
}
```

It appears under a heading naming your tools, so the model can tell which calls it governs. Read
fresh each turn: load a plugin mid-session and its guidance arrives with its tools; unwire it and
both go together.

**Write it for the model, not for a reader of logs.** The model sees a flat list of tools and has no
concept of a plugin, so "this plugin talks to a language server" names something it cannot act on.
Say what changes what it would do — a position convention, a call that must come first, what an
empty result means.

### A plugin with no tools at all

`"tools": []` is valid. The plugin loads, offers nothing callable, and contributes only its
`instructions` — a way to ship prompt text as a versioned artifact that can be installed, approved
and removed as a unit.

```json
{ "name": "house-style", "version": "1.0.0", "spawns": false,
  "instructions": "Prefer records over classes for data.", "tools": [] }
```

**Prefer `CXAGENT.md` unless you need what a plugin gives you.** A project instruction file needs no
binary, no approval and no install — for one repository's conventions it is the lighter answer. A
plugin earns itself when the text must travel with a version, reach many checkouts, or be turned on
and off without editing a file.

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

**cxagent asks once, at load, whether to trust the binary.** That prompt names the plugin, says what
it will contribute — how many tools, and whether it adds guidance to the model's instructions — and
shows a content hash covering its whole load set. Change any byte and the user is asked again. This
is the only boundary cxagent can enforce on your behalf, and nothing in config can pre-approve it.

The contribution matters to the person answering: a plugin that adds no tools and only prompt text
shapes every later turn without ever appearing as a tool call they can watch.

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

**When the ARGUMENTS decide, use `"gated": "dynamic"`.** Some tools are dangerous only sometimes. A
query tool's `SELECT` is a read and its `DROP TABLE` is not; a file tool is harmless inside the
workspace and worth a question outside it. A boolean fixed before the call cannot tell those apart,
so it forces you to choose between asking about every harmless call — noise users escape by
disabling gating wholesale — and asking about none of them.

`"dynamic"` routes each call through a method that sees the arguments:

```json
{ "name": "db_query", "gated": "dynamic" }
```

```csharp
public sealed class QueryPlugin : IPlugin, IPluginGateSource
{
    public PluginGate? Gate(string toolName, JobParameters call)
    {
        var sql = call.Get("sql", "");
        return sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            ? null                                   // a read: no prompt
            : new PluginGate($"run: {sql}");         // anything else: ask, and show what
    }
}
```

Returning null means no prompt. Returning a `PluginGate` asks, and its `Display` is yours to write —
you saw the arguments, so name the file or the statement rather than only the tool.

You supply the wording; cxagent decides the scope. A `PluginGate` carries no permission kind and no
"always" rule, because those decide what a stored grant would cover — and a plugin that could set
them could turn a prompt about its own tool into a grant over shell commands or files it does not
own.

**Declaring `"dynamic"` without implementing `IPluginGateSource` fails the load.** The sidecar told
the user this tool decides per call; a plugin that then never decides would make that promise
unfalsifiable.

### The one field the host checks before your code runs

```json
{ "pluginContract": 2, "name": "my-plugin", ... }
```

**Required, and checked with exact equality.** It says which shape you were built against — the
manifest fields, the gating vocabulary, the lifecycle. A host cannot know whether an unfamiliar
contract omits something whose absence changes behaviour silently, so it refuses rather than
guesses. Omit it and the load is refused too: a manifest that does not say what it was built against
cannot be checked, and assuming compatible is the least safe reading available.

Exact equality cuts both ways, and that is deliberate — a contract-1 plugin is refused by a
contract-2 host exactly as a contract-3 one would be. One comparison answers both directions.

**There is no version floor beside it**, and adding one would be a step backwards. What a plugin
needs is never really "cxagent 0.9.5"; it is "a host that understands `dynamic`" — which *is* the
contract. A version is a proxy that can be satisfied by a build whose number is high enough but
which dropped the feature.

**A cxagent release does not invalidate your build.** `CxAgent.Core` freezes the `AssemblyVersion` a
managed plugin binds to, so the reference you compiled against resolves against every later release;
the release number rides on `FileVersion` and `InformationalVersion`, which the loader does not
consult. Build once against any version, and the contract integer above is what decides whether you
load — which is the guarantee an ABI plugin has always had for free, since a `.so` references nothing
of ours at all.

That freeze is why this section is true rather than aspirational: an assembly reference resolved by
exact version would refuse your plugin with `Could not load file or assembly` *before* any of the
checking described here happened, and say nothing about compatibility.

It is read from the sidecar **before your assembly is loaded**, let alone constructed. That is the
only placement worth having: loading an assembly is irreversible and a constructor is arbitrary
code, so a check after either discards a result rather than preventing anything.

**Check it back, from `Load`.** The host refuses a contract it does not know — but a host OLDER than
your contract has never heard of it, and reads your manifest with its own rules. A cxagent that
predates `"dynamic"` takes it for `false` and offers your tools ungated; nothing fails, the gate is
simply absent. Only you can catch that:

```csharp
public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
{
    if (context.HostContract < 2)
        throw new NotSupportedException($"needs contract 2; this host speaks {context.HostContract}.");
    ...
}
```

A throw from `Load` fails the load and names the reason. `csharp-lsp` does exactly this, because its
three tools gate per call and a host that cannot see that would run them without asking.

`IPluginContext.HostVersion` is cxagent's own version, for logging or display — not how
compatibility is decided.

### The manifest and the callback

The two answer different questions, and the manifest wins where they disagree.

Your sidecar is read **before your assembly loads** — that is what lets cxagent show a user what you
claim without running you, and what the load prompt summarises. `Gate` runs **after** they approved
that, once per call.

So the manifest is a ceiling and the callback narrows beneath it:

| `gated` | `Gate()` returns | result |
|---|---|---|
| `true` | not called | asks — always |
| `false` | not called | never asks |
| `"dynamic"` | `null` | no prompt |
| `"dynamic"` | a `PluginGate` | asks, with your wording |

A `true` tool asks even if your code would rather it did not, and `alwaysAskable: false` cannot be
widened back at runtime. Otherwise the sidecar a user read before approving would be a claim your
code could quietly abandon.

**A gate that fails asks anyway.** Throw, hang, or return something unparseable and cxagent prompts
with a generic description and no "Always" — a broken gate is noisy rather than silently permissive,
and cannot earn a standing grant while it is broken. Never return null to signal an error: null
means "this call is fine".

The calculator examples gate addition, never gate multiplication, and gate division only when the
divisor is zero. The first two are absurd on their own, and that is the point: a gate on `2 + 2` can
only teach you the mechanism. The third is the one that shows why the mechanism exists — same tool,
same schema, different answer, decided by the arguments.

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
project's copy wins over a globally installed one. Each of those is searched itself and one level
down, so `plugins/calculator/calculator.dll` is found the same as a loose `calculator.dll` sitting
in the folder. **Ship in a directory of its own** — a plugin's identity is a hash over everything in
its load-set folder, and .NET resolves its dependencies from that same folder, so a folder shared
with another plugin means neither is isolated and installing either one re-asks the other's load
prompt.

`enabled: false` means no process, no tools, no prompt, nothing. It is configuration, not permission:
loading still asks, every time. `/plugin load <name> --once` overrides `enabled: false` for one
session and still asks.

A note on where to put them: some language servers crawl the workspace for projects and will happily
index a plugin binary sitting in `.cxagent/plugins`. The global folder avoids that.

## See also

- [`IPlugin.cs`](https://github.com/nickprotop/cxagent/tree/master/cxagent.Core/Core/Plugins/IPlugin.cs) — the managed contract, documented per method
- [`Abi/cxagent_plugin.h`](https://github.com/nickprotop/cxagent/tree/master/cxagent.Core/Core/Plugins/Abi/cxagent_plugin.h) — the seven C functions, with the ownership rules
- [`Abi/README.md`](https://github.com/nickprotop/cxagent/tree/master/cxagent.Core/Core/Plugins/Abi/README.md) — the JSON envelopes crossing the ABI boundary

Hosting a plugin rather than writing one:

- [`docs/api.md`](api.md#plugins) — `LoadPlugin`, the registry, the loader, child-process reaping
- [`docs/tools.md`](tools.md) — the other kind of tool: compiled into your app rather than loaded
- [the package README](../README.md#tools-that-arrive-at-run-time) — where plugins sit among everything else
