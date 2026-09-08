# experiment-managed

Exercises `IPluginClient` and nothing else.

**NOT IN THE CATALOG.** `plugins.json` does not list this plugin — nobody installs an experiment by
accident. It exists to be driven, not used.

## Why

The three plugins cxagent ships evaluate expressions, find clones, and wrap a language server. None
of them declares the client capability contract 3 added, and none of them will — none has a reason
to start work in its own session. Without a plugin that does, a drive of the published set proves the
old contract twice and the new one never.

## The one tool

`experiment_submit(goal, want_result)` — calls `IPluginClient.Submit(goal, want_result)` in this
plugin's own session and reports what came back:

- `want_result` omitted or `false`: reports whether the goal was accepted.
- `want_result: true`: waits for the turn and reports its final answer too.

A refusal (queue full, no client because this host predates contract 3) comes back as a failed call,
not a successful one that merely describes a problem.

## The one command

`/experiment-say <text>` — echoes `text` back into the transcript. It exists so a drive exercises a
plugin command as well as a plugin tool: the tool above is something a model calls, this is something
a person types, and cxagent dispatches the two through different entry points
(`IPlugin.Invoke` vs. `IPluginCommandHandler.RunCommand`).

## What it deliberately does not do

Grow. An experimental plugin that grows features grows reasons for its own bugs, and then a failed
drive tells you nothing about the contract it was meant to exercise.
