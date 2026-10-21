# Plugin catalog

Plugins cxagent releases and supports — what they do, how to install one, and how to turn it on.
Each has its own page with its download and settings.

| plugin | what it does | needs | |
| --- | --- | --- | --- |
| **csharp-lsp** | Go-to-definition, find-references and diagnostics for C#, across project boundaries | a C# language server (`csharp-ls` by default) | [details](csharp-lsp/README.md) |
| **calculator** | Arithmetic a model can trust — one tool takes a whole expression | nothing | [details](calculator/README.md) |
| **triggers** | Work that starts without anybody typing — schedules a prompt to arrive later in the same session, on a delay, a time or a cron line | nothing | [details](triggers/README.md) |
| **clone-finder** | Duplicated code found without reading the repository — one tool returns the duplicated blocks ranked by size | nothing | [details](clone-finder/README.md) |
| **repo-map** | A map of a codebase without reading it — one tool returns the most-used files' declarations, with line numbers, ranked and cut to a budget | nothing | [details](repo-map/README.md) |
| **worktree** | A git worktree for a sub-agent to work in, then its diff, a merge into your branch or a discard — merge and discard ask first | git | [details](worktree/README.md) |

## Installing one

The plugin manager (**F2**) installs any plugin listed here into your config folder's `plugins/`,
verifying its checksum on the way. Any plugin can also be taken from a release by hand — see its own
page for the asset name and the config entry.

**Installed is not enabled.** cxagent reports a plugin it finds but has no configuration for, and
leaves it alone: loading runs code cxagent did not write, so it asks once, showing a hash of the
plugin's whole load set. Enable one with `/plugin load` for a single session, or a `config.json`
entry to keep it.

## Where cxagent looks

Three folders, in this order, first match wins:

| | |
| --- | --- |
| 1 | anything in `pluginPaths`, in the order you wrote it |
| 2 | `<config>/plugins` — `~/.config/cxagent/plugins`, or `%APPDATA%\cxagent\plugins` |
| 3 | `<project>/.cxagent/plugins` — the repo you are working in |

The last two are always searched, so a plugin dropped in either is found with no `pluginPaths` entry
at all. Global before project means a repository you cloned cannot stand in for a plugin you installed
by shipping a file of the same name; a plugin only the project has is still found, and asks before it
runs.

Each of those is searched **itself and one level down**, so a plugin in a directory of its own —
`plugins/csharp-lsp/csharp-lsp.dll` — is found exactly as one sitting loose in the folder is.

**A directory of its own is the layout to prefer.** A plugin's identity is a hash over everything in
its load-set folder, and .NET resolves its dependencies from that folder too, so two plugins sharing
one are neither isolated from each other nor separately identifiable: installing or updating either
one changes the other's hash and re-asks its load prompt. The manager writes the nested layout, and
a loose plugin keeps working — cxagent says so at load rather than refusing it.

A relative `pluginPaths` entry resolves against **the project directory**, not the config folder — so
`".cxagent/tools"` means the repo you are in, and an absolute path or `~` means what it says.

> **A plugins folder inside a repo is a folder your tools will read.** Some language servers scan a
> workspace for projects and will happily index a plugin binary sitting in it — OmniSharp hangs on a
> `.cxagent/plugins` folder in a repo with no solution file. Prefer the global folder unless a plugin
> genuinely belongs to one project.

## Configuring one

```json
{
  "pluginPaths": ["~/.config/cxagent/plugins"],

  "plugins": {
    "csharp-lsp": {
      "file": "csharp-lsp.dll",
      "enabled": true,
      "settings": { "server": "csharp-ls", "args": [] }
    }
  }
}
```

What each key means, where cxagent looks, and what `enabled` does and does not decide are in
[CONFIG.md](../CONFIG.md#plugins-and-pluginpaths--plugins). The name you key it under is yours — it
is what `/plugin` lists and `/plugin unwire` takes.

**config.json is read at startup.** Editing it does nothing until you restart. Use `/plugin load`,
which takes settings inline, to try something without one:

```
/plugin load csharp-lsp.dll { "server": "/opt/omnisharp/OmniSharp", "args": ["-lsp"] }
```

Nothing `/plugin load` does persists — that is the point of it. Once you know you want a plugin,
write the config entry.

## Other plugins

Nothing requires a plugin to live here. Anything you drop in the plugins folder — or point
`pluginPaths` at — is discovered, reported and approved exactly the same way; this directory is
only what cxagent itself releases and supports.

## Adding one to this catalog

Anyone's plugin is added through **[publishing a plugin →](../cxagent.Core/sdk/docs/publishing.md)** —
pack, upload, verify, submit. One built in this repository has its own steps there, under
[first-party plugins](../cxagent.Core/sdk/docs/publishing.md#first-party-plugins).

*Writing one? Start at [the SDK](../cxagent.Core/sdk/README.md) — the tutorials, the contract, the
command line, testing and publishing.*

*[`plugins.json`](plugins.json) is this page as data, for the plugin picker to read.*
