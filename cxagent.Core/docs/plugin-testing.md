# Testing a cxagent plugin

*Check a plugin folder and run real calls through it — from the command line, or from a C# test —
with no session and no model.*

Two ways in, one engine. `cxagent plugin check` and `cxagent plugin call` run on the same code as
the **CxAgent.Plugins.Testing** package. Both use the loaders a session uses, and both ask the
same permission question a session asks. A plugin that passes here loads, and asks, the way it
will for a user.

Both take a plugin **folder**: one `<name>.plugin.json` and the library of the same stem beside it
(`.dll`, `.so` or `.dylib`), plus whatever that library needs. That is what a user installs.

## `cxagent plugin check <folder> [--json]`

Every load-time check, without a session:

- the sidecar parses;
- its contract is one this cxagent loads, and its features fit that contract (`"client"` needs 3,
  `"kind"` needs 4);
- the kind fits the file;
- no tool takes a built-in name.

Then it **loads** the plugin, as a session would, without starting it. That load compares what
the code returns with the sidecar. A native plugin's load also covers the plugin host's handshake,
`describe` and version.

It reports every problem, not the first, one line each, naming the field:

```
$ cxagent plugin check weather/
kind: "native", but weather.dll is a .NET assembly.
tools[0].name: 'read_file' is a built-in tool name; a plugin cannot take it.
```

`--json` gives `{"ok": false, "problems": [{"field": "kind", "message": "…"}]}`.

## `cxagent plugin call <folder> <tool> ['<json>'] [options]`

The real lifecycle once: **Load, Start, the gate, Invoke, Stop.**

```
$ cxagent plugin call weather/ search '{"city":"Athens"}' --answer allow
progress 50% fetching
asks: look up the weather for Athens
answered: allow
{
  "success": true,
  "exitCode": 0,
  "errorMessage": null,
  "output": { "content": "21°C, clear in Athens" }
}
```

**The gate is the session's own decision.** If the call would ask the user, `call` prints the
question. Without `--answer` it stops there, as a session waits at its prompt:
`stopped: a user would be asked — pass --answer allow or --answer deny`. `--answer deny` refuses it
the same way, and the tool never runs.

**Reports print as they happen.** Progress, log lines and streamed text appear while the call runs,
and the result follows once it returns. A submit from the plugin's client prints as
`would start a turn: <goal>`. Nothing reaches a model.

| Option | |
|---|---|
| `'<json>'` | the tool's arguments; must be a JSON object. Default `{}` |
| `--settings '<json>'` / `--settings-file f` | the plugin's `settings` block, as in config.json (one or the other) |
| `--cwd d` | the folder the plugin works in. Default: the current one |
| `--answer allow\|deny` | how the permission question is answered |
| `--timeout s` | stop the call after `s` seconds; the plugin is then stopped and its processes reaped. Default 60 |
| `--command name [args…]` | run a command the plugin declares instead of a tool; everything after the name is its argument |
| `--json` | one JSON object — `gate`, `answer`, `stoppedAtGate`, `reports`, `submits`, `result` — and nothing printed live |

### Exit codes

Both commands share them:

- **0**: success. For `call`, this includes stopping at an unanswered or denied gate: a session
  would stop there too.
- **1**: the plugin is at fault. That means a problem `check` found, a load or Start that failed, a
  result whose `success` is false, a refused command, or a call that overran its timeout.
- **2**: the command line is wrong.

A C plugin's CTest can run it directly:

```cmake
add_test(NAME search COMMAND cxagent plugin call ${CMAKE_BINARY_DIR}/plugin search "{\"city\":\"Athens\"}" --answer allow)
```

## CxAgent.Plugins.Testing

The same, inside xunit, NUnit or MSTest:

```xml
<PackageReference Include="CxAgent.Plugins.Testing" Version="0.9.*" />
```

```csharp
using CxAgent.Plugins.Testing;

[Fact]
public async Task SearchAsksAndAnswers()
{
    Assert.Empty(await PluginHarness.CheckAsync(PluginDir));          // what check reports

    await using var plugin = await PluginHarness.LoadAsync(PluginDir);
    var call = await plugin.CallAsync("search", new { city = "Athens" }, answer: GateAnswer.Allow);

    Assert.Equal("look up the weather for Athens", call.Gate?.Display);
    Assert.True(call.Result!.Success);
    Assert.Contains(call.Progress, p => p.Percent == 50);
}
```

- **`PluginHarness.CheckAsync(folder)`** returns the problems `check` prints, as
  `PluginProblem(Field, Message)`. The list is empty when the plugin loads.
- **`PluginHarness.LoadAsync(folder, options)`** loads and starts the plugin. It throws
  `PluginLoadException`, whose `Problems` say why, when it can't. `HarnessOptions` carries
  `Settings`, `WorkingDirectory` and `HostDllPath`.
- **`plugin.CallAsync(tool, args, answer, onReport, ct)`** returns a `HarnessCall`:
  - `Gate` is the question, or null when the call asks nothing.
  - `Answer` and `StoppedAtGate` say what happened at it.
  - `Result` is the tool's result, or null when the call never ran.
  - `Reports`, with the `Progress`, `Logs` and `Streamed` views, holds what the plugin reported, in
    order.
  - Arguments may be an object, a JSON string or a `JsonElement`. The tool receives them as the
    model's JSON arrives, so `Get<T>` converts exactly as it does in a session.
- **`plugin.CommandAsync(name, arguments)`** runs a declared command.
- **`plugin.Submits`** holds the goals the plugin submitted, and **`plugin.Logged`** its own log.
- **Disposing** stops the plugin and kills any process it registered and left running.

**Native plugins too.** The package carries the plugin host and copies it beside your test, so a
C library loads and is called just as it would be in cxagent.

A tool the manifest does not declare is refused with an `ArgumentException` naming the ones it
does, before your plugin sees the call. A session never calls such a name either.
