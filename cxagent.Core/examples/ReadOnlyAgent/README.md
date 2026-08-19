# ReadOnlyAgent — taking tools away

An agent that answers questions about a codebase and cannot change it. Four tools, no writes, no
shell, no sub-agents.

```bash
dotnet run --project cxagent.Core/examples/ReadOnlyAgent -- /path/to/repo
```

The interesting part is one line:

```csharp
var readOnly = new ToolSelection([Tool.ReadFile, Tool.Glob, Tool.Grep, Tool.TodoWrite]);
```

## Why not just tell it?

A briefing that says *"never edit files"* is a request. Models do not reliably follow instructions
they are given, and nothing checks. A selection is a list the model is never offered — and if it
guesses `write_file` anyway, the call is **refused**, not silently ignored:

```
write_file is not available.
```

That is a different answer from `no such tool`, which is what a typo gets. One means stop; the other
means try another name.

## Why a whitelist rather than subtracting

`["inherited", "-write_file", "-replace_in_file"]` reads more naturally and is the wrong choice here.
A bare list names what this agent **may** have, so a tool added to a future version of the library is
not silently granted. Subtracting means revisiting the line every time the built-in set grows, and
forgetting to is a failure that does not announce itself.

Use `inherited` when you are narrowing a set you control. Use a whitelist when you are stating a
boundary.

## Why `run_shell` is absent

`cat` reads a file. `rm` does not. A shell is the one tool whose reach cannot be narrowed by naming
it, so leaving it in would make the rest of the list decorative — the model would route around the
missing tools without meaning any harm.

That is not hypothetical. On a live drive with `web_fetch` withheld and `run_shell` available, the
model reached for `curl` within one turn. It was not being clever; a shell was simply the tool it had.

## Why there is no permission gate

`SessionManager.Create` without a `buildGate` leaves every call ungated. For an agent that can write,
that would be reckless. For this one there is nothing to intercept: the four tools read files inside
a working directory you chose.

A prompt that never fires teaches a user to press Enter without reading it. **The selection is doing
the work the gate would otherwise do** — add a gate the moment you add a tool that changes something.
[ToolAgent](../ToolAgent) has one.

## Single mode

The `agent` tool is not in the selection, so the session is opened in `AgentMode.Single`. Asking for
fan-out anyway is a contradiction the library resolves for you — it falls back and says so — but
stating it here means the mode and the toolset agree from the first turn rather than after a
correction.

## Where this fits

| Example | Shows |
|---|---|
| [ToolAgent](../ToolAgent) | adding a tool, and the two permission gates |
| [SpectreAgent](../SpectreAgent) | a rendered front end in about a hundred lines |
| **ReadOnlyAgent** | taking tools away, and when a boundary beats a briefing |

[Tool selection in full →](../../docs/api.md#tool-selection)
