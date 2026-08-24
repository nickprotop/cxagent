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

A plugin here is one cxagent builds, releases and installs, so it carries the same expectations as
the app itself. Writing the plugin is [the dev guide's
job](../cxagent.Core/docs/plugins.md); getting it into the catalog is these five steps.

**1. The directory.** `plugins/<name>/`, with the project, its sidecar, and a `README.md` covering
what the plugin does, the release asset to download, anything the USER must install separately, and
every setting. Follow [csharp-lsp](csharp-lsp/README.md).

**2. The catalog entry**, in [`plugins.json`](plugins.json). Its `tools` and `spawns` must match the
plugin's own sidecar — the file is read by the plugin picker, and an entry that disagrees with the
plugin describes something that does not exist.

**3. The release build**, in `.github/workflows/release.yml`. A managed plugin with no
`RuntimeIdentifier` is portable MSIL, so it needs ONE build, not a matrix entry — six RIDs would
upload six identical files. Build rather than publish (`Private="false"` keeps `CxAgent.Core` out of
the output, and a publish would pull in its whole dependency tree), then zip the DLL with its
sidecar. They must ship together: the sidecar is read before the assembly is loaded, so a zip
missing it produces a plugin that cannot load and cannot be described.

**4. The installer**, in `install.sh`, if it should be installed by default. Download from the
release pinned to `$TAG` — a plugin built from a later commit than the binary it plugs into is a
skew that surfaces as a puzzling failure — and keep it best-effort, so an older release without the
asset still installs cxagent itself. Note that `install.sh` installs named plugins, not the whole
catalog: a plugin can be in the catalog and left for the user to fetch.

**5. Nothing else.** In particular, do not add a config entry anywhere. An installer that enabled a
plugin would be enabling code the user was never asked about; cxagent reports it as
present-but-unconfigured and they decide.

> The plugin's name appears in the workflow and the installer by hand, in about six places. That is
> fine for a catalog this size and is the first thing worth generating from `plugins.json` if it
> grows.

*Writing one? That is [`cxagent.Core/Core/Plugins`](../cxagent.Core/docs/plugins.md) — the
contract, the sidecar format, permission, settings, and two worked examples.*

*[`plugins.json`](plugins.json) is this page as data, for the plugin picker to read.*
