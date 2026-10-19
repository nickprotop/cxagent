# The C plugin SDK

*Write tools in C. cxp handles the ABI, the JSON and the memory.*

A native plugin is a shared library that exports the functions in
[`cxagent_plugin.h`](../sdk/c/cxagent_plugin.h). The C SDK, **cxp**, implements every export for you
over a table of the tools you register. You write functions that take a call and return a result.

| File | |
|---|---|
| `cxp.h` | The API, the only header you include |
| `cxp.c` | Every ABI export, over your table |
| `cxp.cmake` | `cxp_add_plugin(...)`: builds the library from your sidecar |
| `cJSON.h`, `cJSON.c` | JSON, vendored ([cJSON](https://github.com/DaveGamble/cJSON) 1.7.18, MIT) |
| `cxagent_plugin.h` | The raw ABI, if you would rather not use cxp |

`cxagent plugin new <name> --lang c` writes a project with these files copied into `cxp/`. The
project carries its own SDK, so it builds with nothing but a C compiler and CMake 3.20 or later.
Walk through one in the [SDK tutorial](../sdk/README.md#tutorial-c-wordcount).

## A plugin

```c
#include "cxp.h"

static cxp_result count(const cxp_call *call) {
    const char *file = cxp_arg_string(call, "file");
    if (!file) return cxp_error("pass a file to count.");
    cxp_progress(call, 50, "half way");
    return cxp_ok_text(cxp_format("%s: 12 lines", file));
}

static cxp_gate count_gate(const cxp_call *call) {
    return cxp_ask(cxp_format("read %s", cxp_arg_string(call, "file")));
}

CXP_PLUGIN(
    CXP_TOOL("wordcount_count", count, count_gate)
)
```

```cmake
cmake_minimum_required(VERSION 3.20)
project(wordcount C)
include(cxp/cxp.cmake)
cxp_add_plugin(wordcount SIDECAR wordcount.plugin.json SOURCES wordcount.c)
```

The sidecar (`wordcount.plugin.json`) declares what the table registers, with `"kind": "native"`.
It is the one copy of the manifest; [plugins.md](plugins.md#the-sidecar) covers its fields.

## What `cxp_add_plugin` does

- **Embeds the sidecar** as what `describe` returns, and takes its `pluginContract` as the
  handshake. The library and its sidecar cannot disagree.
- **Exports what the sidecar asks for.** `poll` is compiled in only with `"client": true`, and
  `command` only when `"commands"` lists any. cxagent polls any library that exports `poll`, so
  exporting it unasked would cost every plugin a timer.
- **Names the library after the sidecar** (`wordcount.so`, `.dylib` or `.dll`, with no `lib`
  prefix) and copies the sidecar beside it, because cxagent finds one from the other.
- **Refuses a sidecar it cannot read,** or one whose `pluginContract` is not a number or whose
  `client` is not true or false. The configure step fails, naming the file.
- **Leaves the library in the build folder** with every generator, multi-config ones included, so
  `cxagent plugin check build` finds it.

## The API

### Registering

| | |
|---|---|
| `CXP_PLUGIN(entries...)` | The table: one per plugin, in one source file |
| `CXP_TOOL(name, run, gate)` | `run` is `cxp_result (*)(const cxp_call *)`; `gate` is `cxp_gate (*)(const cxp_call *)`, or `0` for a tool gated `true` or `false` |
| `CXP_COMMAND(name, run)` | `run` is `cxp_command_result (*)(const char *arguments)` |

A tool's name and its `"gated"` value come from the sidecar. `"gated": "dynamic"` calls your gate
function. `true` always asks, and `false` never does. A `"dynamic"` tool registered with `0` asks on
every call, naming the tool: the sidecar said its calls may need permission, so cxp does not let them
through unasked.

### Arguments and context

| | Returns |
|---|---|
| `cxp_arg_string(call, name)` | the string, or NULL if missing or not a string |
| `cxp_arg_number(call, name, fallback)` | the number, or `fallback` |
| `cxp_arg_bool(call, name, fallback)` | 1 or 0, or `fallback` |
| `cxp_arg_json(call, name)` | the raw `cJSON` value, or NULL; cxp owns it |
| `cxp_working_directory()` | the folder the plugin works in |
| `cxp_setting_string` / `_number` / `_bool` | the plugin's `settings` block in config.json, with the same fallbacks |

A mistyped argument returns the fallback and never crashes. Treat input from the model as untrusted:
it is.

### Results

| | |
|---|---|
| `cxp_ok_text(text)` | success; `text` is what the model reads |
| `cxp_ok_json(output)` | success with structured output: a `cJSON` object cxp takes ownership of; its `"content"` is what the model reads. Anything but an object is a failed call saying so |
| `cxp_error(message)` | the tool failed on its own terms. The model sees `message`, so say what to do differently |

A tool that returns an empty `cxp_result` becomes a failed call naming the tool. It is never a crash.

### Gates and commands

| | |
|---|---|
| `cxp_no_ask()` | this call asks nothing |
| `cxp_ask(display)` | asks with your words, and "Always" is offered |
| `cxp_ask_every_time(display)` | asks, and "Always" is never offered |
| `cxp_reported(message)`, `cxp_changed(message)`, `cxp_refused(message)` | a command's result |

### Reporting while a call runs

| | |
|---|---|
| `cxp_progress(call, percent, message)` | shown on the call's row |
| `cxp_stream(call, text)` | text as it is produced, behind the row's expand |
| `cxp_log(call, level, line)` | the call's log; `level` 0 Trace … 4 Error. With a NULL call, outside any call, the plugin's own log |
| `cxp_call_id(call)` and `cxp_progress_any`, `_log_any`, `_stream_any` | for a thread of your own: copy the id during the call, then report with it. A report after the call returned is dropped |

### The client

`cxp_submit(goal)` queues a turn to start, as the user typing `goal` would. It needs
`"client": true` in the sidecar, and returns 0 when queued.

### Memory and threads

- **Free nothing cxp gives you.** `cxp_format(...)` is printf into a string that lives until the
  current call, gate or command returns. Results, gates and command results belong to cxp once
  returned; build them with the functions above, never by hand, since cxp frees what they hold.
- **On a thread of your own, use your own buffers.** The `cxp_format` arena is per thread and only
  cxp's calls release it, so strings formatted on your thread are never freed.
- **Calls can run at once,** each on its own thread. Keep shared state behind your own lock. A
  `cxp_call` is valid only during its call.

## Building for every platform

The template's `.github/workflows/build.yml` builds `linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64`, `win-x64` and `win-arm64`. On a version tag it runs `cxagent plugin pack` with one
`--native <lib>=<rid>` per build and attaches the zips and the catalog entry to the release. Locally:

```
cmake -B build && cmake --build build
cxagent plugin pack build --native build/wordcount.so=linux-x64
```

## The raw ABI

Without cxp, implement the functions in `cxagent_plugin.h` yourself. The JSON each one exchanges is
in [Abi/README.md](../Core/Plugins/Abi/README.md), and
[`examples/CalculatorAbiPlugin`](../examples/CalculatorAbiPlugin) is a complete example with no
dependencies. Mark every export `CXAGENT_PLUGIN_EXPORT`, and set `"kind": "native"` in the sidecar:
on Windows a native plugin and a .NET one are both `.dll`.
