# Testing a cxagent plugin

*Check a plugin folder and run real calls through it — from the command line, or from a C# test —
with no session and no model.*

Two ways in, one engine. `cxagent plugin check` and `cxagent plugin call` run on the same code as
the **CxAgent.Plugins.Testing** package. Both use the loaders a session uses, and both ask the
plugin's own permission question — `gated` in the sidecar, and your `Gate` for `"dynamic"` — through
the code a session uses. A plugin that passes here loads, and asks, the way it will for a user.

**What a session adds on top is cxagent's, not yours.** Before any plugin tool runs, a session in
an asking mode first asks whether to use the tool at all ("use the search tool in this folder"),
unless the user already granted it. Your plugin cannot change that question, so the harness leaves
it out: a `"gated": false` tool asks nothing here, yet still prompts once in a fresh session.

Both take a plugin **folder**: one `<name>.plugin.json` and the library of the same stem beside it
(`.dll`, `.so` or `.dylib`), plus whatever that library needs. That is what a user installs.

## From the command line

`cxagent plugin check <folder>` runs every load-time check, and `cxagent plugin call <folder> <tool>`
runs one real call through Load, Start, the gate, Invoke and Stop, printing the permission question
and stopping there unless `--answer allow|deny` answers it. Every option, the `--json` shapes and the
exit codes are in [cli.md](cli.md).

A C plugin's CTest can run it directly:

```cmake
add_test(NAME search COMMAND cxagent plugin call ${CMAKE_BINARY_DIR}/plugin search "{\"city\":\"Athens\"}" --answer allow)
```

## Then: `pack` and `verify`

A plugin that passes `check` and `call` is ready to package. `cxagent plugin pack <folder>` writes
the zips and the catalog entry, and `cxagent plugin verify <entry>` checks what a user's install
would get; `--https-only` refuses a local file, as the submission bot does. Neither runs any plugin
code. Both share [the exit codes](cli.md#exit-codes). See the [C tutorial](../README.md#tutorial-c-wordcount), and
[Publishing a plugin](publishing.md) for the way to the catalog.

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
  `Settings`, `WorkingDirectory`, `HostDllPath` and `StopTimeout`, how long disposing waits for the
  plugin's Stop (10 seconds by default).
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
- **Disposing** ends the plugin's `Lifetime`, stops it, and kills any process it registered and
  left running. A Stop that outlasts `StopTimeout` is abandoned and reported in `StopError`.
- **A call is waited on as a session waits on it.** Cancelling its token stops the wait even if the
  plugin never reads the token. A tool that throws gives a failed `Result`, not an exception.

**Native plugins too.** The package carries the plugin host and copies it beside your test, so a
C library loads and is called just as it would be in cxagent.

A tool the manifest does not declare is refused with an `ArgumentException` naming the ones it
does, before your plugin sees the call. A session never calls such a name either.
