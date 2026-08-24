# Plugin catalog

Plugins cxagent releases and supports. One directory each, and
[`plugins.json`](plugins.json) beside them as the machine-readable version of this page — what the
plugin dialog will read rather than parsing prose.

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

## Adding a plugin to this catalog

A plugin here ships as a release asset and gets installed by the installer, so it carries the same
expectations as the app. Add its directory, a `README.md` documenting the asset and configuration,
and an entry in `plugins.json`.

Nothing requires a plugin to live here. A third-party plugin is a DLL and a sidecar dropped in the
plugins folder or named by `pluginPaths`; it needs nothing from this directory and gets the same
treatment at load.

Two rules if you do add one:

**Name its tools for what makes them yours.** A tool name is claimed session-wide and two plugins
cannot share one — `csharp_definition`, never `lsp_definition`, or the next language-server plugin
cannot load beside this one.

**Reference `CxAgent.Core` with `Private="false"`.** The host process already has it loaded; a copy
beside the plugin is dead weight in every release asset.

See [the plugin guide](../cxagent.Core/Core/Plugins/README.md) for the contract, the sidecar format,
permission, settings, and two worked calculator examples.
