# Examples

Five examples, each showing one thing.

The four .NET projects reference `cxagent.Core.csproj` directly rather than the package, so a
breaking change to the API breaks these builds instead of being found after a release. The fifth is
a single C file with no project at all — it talks to cxagent through a C ABI, which is the point of
it.

## Building a front end

| | |
| --- | --- |
| [**SpectreAgent**](SpectreAgent) | A second front end in about a hundred lines: a prompt, streamed text, one line per tool. The proof that nothing in the library assumes a terminal. |

## Choosing what an agent can do

| | |
| --- | --- |
| [**ToolAgent**](ToolAgent) | Three tools of its own, offered beside the built-ins — and what each one's `Gate` returns, which is the interesting line. |
| [**ReadOnlyAgent**](ReadOnlyAgent) | The other direction: a selection that leaves an agent unable to write, shell out or delegate, printing every call so you can watch it hold. |

## Writing a plugin

A plugin is tools loaded from a DLL at run time, rather than compiled into your app. The same
calculator twice, so the diff between them is what the process boundary costs and nothing else:

| | |
| --- | --- |
| [**CalculatorPlugin**](CalculatorPlugin) | Managed, ~110 lines. The whole `IPlugin` contract, with a permission prompt for adding two numbers so the gate is impossible to miss. |
| [**CalculatorAbiPlugin**](CalculatorAbiPlugin) | The same calculator in one file of C, for a plugin your language cannot write managed. No JSON library, so you see the bytes crossing the boundary. |

Write managed if you are writing C#. See [the plugin guide](../docs/plugins.md).

## Running one

```bash
dotnet run --project cxagent.Core/examples/SpectreAgent
```

The plugin examples are libraries, not programs — they are loaded by cxagent rather than run. Each
one's README says how.
