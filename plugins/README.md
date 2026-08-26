# Plugin catalog

Plugins cxagent releases and supports — what they do, how to install one, and how to turn it on.
Each has its own page with its download and settings.

| plugin | what it does | needs | |
| --- | --- | --- | --- |
| **csharp-lsp** | Go-to-definition, find-references and diagnostics for C#, across project boundaries | a C# language server (`csharp-ls` by default) | [details](csharp-lsp/README.md) |

## Installing one

`install.sh` places `csharp-lsp` in your config folder's `plugins/` automatically. Any plugin here
can also be taken from a release by hand — see its own page for the asset name and the config entry.

**Installed is not enabled.** cxagent reports a plugin it finds but has no configuration for, and
leaves it alone: loading runs code cxagent did not write, so it asks once, showing a hash of the
plugin's whole load set. Enable one with `/plugin load` for a single session, or a `config.json`
entry to keep it.

## Where cxagent looks

Three folders, in this order, first match wins:

| | |
| --- | --- |
| 1 | anything in `pluginPaths`, in the order you wrote it |
| 2 | `<project>/.cxagent/plugins` — the repo you are working in |
| 3 | `<config>/plugins` — `~/.config/cxagent/plugins`, or `%APPDATA%\cxagent\plugins` |

The last two are always searched, so a plugin dropped in either is found with no `pluginPaths` entry
at all. Project before global means a repo's own copy of a plugin shadows an installed one rather
than colliding with it.

Each of those is searched **itself and one level down**, so a plugin in a directory of its own —
`plugins/csharp-lsp/csharp-lsp.dll` — is found exactly as one sitting loose in the folder is.

**A directory of its own is the layout to prefer.** A plugin's identity is a hash over everything in
its load-set folder, and .NET resolves its dependencies from that folder too, so two plugins sharing
one are neither isolated from each other nor separately identifiable: installing or updating either
one changes the other's hash and re-asks its load prompt. `install.sh` writes the nested layout, and
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

| key | |
| --- | --- |
| `file` | the entry-point filename, found in the folders above. Required. |
| `enabled` | default true. `false` means no process, no tools, no prompt — nothing at all. |
| `settings` | handed to the plugin verbatim; cxagent does not read it. See the plugin's own page. |

The name you key it under is yours — it is what `/plugin` lists and `/plugin unwire` takes. It need
not match the plugin's own name, though matching is less confusing.

**`enabled: false` is configuration, not permission.** It answers "should this run at all", and the
approval prompt separately answers "do you trust this binary" — every load asks, and nothing in
config can pre-approve one. `/plugin load <name> --once` overrides `enabled: false` for one session
and still asks.

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

Maintainer work, and its own page: **[releasing a plugin →](RELEASING.md)** — the directory, the
catalog entry, the release build, the installer, and what deliberately does not happen.

*Writing one? That is [`cxagent.Core/Core/Plugins`](../cxagent.Core/docs/plugins.md) — the
contract, the sidecar format, permission, settings, and two worked examples.*

*[`plugins.json`](plugins.json) is this page as data, for the plugin picker to read.*
