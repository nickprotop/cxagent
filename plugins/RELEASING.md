# Releasing a plugin in this catalog

Maintainer procedure. A plugin in [the catalog](README.md) is one cxagent builds, releases and
installs, so it carries the same expectations as the app itself.

Writing a plugin is a different job — [the developer
guide](../cxagent.Core/docs/plugins.md) covers the contract, the sidecar, permission and settings.
This page is only about getting one released.

> **This is the manual version of something planned.** A plugin marketplace is on the
> [roadmap](../ROADMAP.md) — a place to publish an entry from outside this repository, and a dialog
> in cxagent to browse and install one. [`plugins.json`](plugins.json) is already the schema that
> would feed it, which is why it carries publisher, licence, per-platform downloads and hashes for
> a catalog that currently has one entry.
>
> Until then these steps are done by hand, and the schema is the part worth keeping honest: it is
> what a picker will read.


**1. The directory.** `plugins/<name>/`, with the project, its sidecar, and a `README.md` covering
what the plugin does, the release asset to download, anything the USER must install separately, and
every setting. Follow [csharp-lsp](csharp-lsp/README.md).

**2. The catalog entry**, in [`plugins.json`](plugins.json). `name`, `version`, `spawns`, `tools` and `pluginContract`
must match the plugin's own sidecar — `PluginCatalogTests` fails the build if they drift, because the
picker shows what this file says without loading a DLL to check. `tools` carries each tool's `gated`
value as well as its name, so the marketplace can say which tools ask before acting.

`source` says where the plugin comes from, how to download it, and how to check what arrived.

A plugin released here uses `kind: "release"`. It has no fixed URL on purpose — it tracks whichever
cxagent release is being installed — so it carries `urlTemplate` (fill in `{repo}`, `{asset}` and the
tag) plus `latest`, GitHub's always-current alias for a client with no tag in hand. Integrity comes
from the release's own `SHA256SUMS`.

One hosted elsewhere uses `kind: "url"`: a URL fetched as written, and its own `sha256`, because
nothing in this project can vouch for a file it does not build.

**A native plugin is per-platform, and the catalog has to say which.** A managed plugin is portable
MSIL — one build runs everywhere cxagent does, so it says `platforms: ["any"]` and has one `source`.
An ABI plugin is a native library: a `.so` does not load on Windows, and a publisher may ship
`linux-x64` and nothing else. Those entries name the RIDs they actually have and key their downloads
by the same names under `sources`, so a picker can say "not available for your platform" instead of
downloading a library that cannot load. The second worked entry below shows the shape. Its `publisher`, `license` and
`repository` stop being a formality too — nobody needs telling who wrote a plugin that ships in the
same zip as cxagent, and everybody needs telling for one that does not.

### Two worked entries

Neither is real, and neither belongs in `plugins.json` — that file carries only plugins that exist.
Copy the one whose `source` shape matches what you are adding.

**Hosted elsewhere** — a fixed URL, and its own `sha256` because nothing here builds it:

```json
{
  "name": "lsp-rust",
  "displayName": "Rust Language Server",
  "version": "0.3.1",
  "description": "The same three operations for Rust, backed by rust-analyzer.",
  "publisher": "someone-else",
  "license": "Apache-2.0",
  "repository": "https://github.com/someone-else/cxagent-lsp-rust",
  "sourceUrl": "https://github.com/someone-else/cxagent-lsp-rust",
  "readme": "https://github.com/someone-else/cxagent-lsp-rust#readme",
  "source": {
    "kind": "url",
    "url": "https://github.com/someone-else/cxagent-lsp-rust/releases/download/v0.3.1/lsp-rust.zip",
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
  },
  "file": "lsp-rust.dll",
  "kind": "managed",
  "compatibility": {
    "pluginContract": 2,
    "platforms": [
      "any"
    ]
  },
  "spawns": true,
  "tools": [
    { "name": "rust_definition", "gated": false },
    { "name": "rust_references", "gated": false },
    { "name": "rust_diagnostics", "gated": false }
  ],
  "requires": {
    "description": "rust-analyzer on PATH.",
    "default": "rust-analyzer",
    "install": "rustup component add rust-analyzer"
  }
}
```

**Native, and per-platform** — one `source` per RID, named by the same RIDs `platforms` lists:

```json
{
  "name": "ripgrep-tools",
  "displayName": "ripgrep search tools",
  "version": "0.2.0",
  "description": "A native plugin, shown here because an ABI plugin is per-platform and the managed example above cannot show that.",
  "publisher": "someone-else",
  "license": "MIT",
  "repository": "https://github.com/someone-else/cxagent-ripgrep",
  "kind": "abi",
  "compatibility": {
    "pluginContract": 2,
    "platforms": [
      "linux-x64",
      "osx-arm64"
    ]
  },
  "sources": {
    "linux-x64": {
      "kind": "url",
      "url": "https://github.com/someone-else/cxagent-ripgrep/releases/download/v0.2.0/ripgrep-tools-linux-x64.zip",
      "sha256": "1111111111111111111111111111111111111111111111111111111111111111",
      "file": "libripgrep_tools.so"
    },
    "osx-arm64": {
      "kind": "url",
      "url": "https://github.com/someone-else/cxagent-ripgrep/releases/download/v0.2.0/ripgrep-tools-osx-arm64.zip",
      "sha256": "2222222222222222222222222222222222222222222222222222222222222222",
      "file": "libripgrep_tools.dylib"
    }
  },
  "spawns": false,
  "tools": [
    { "name": "rg_search", "gated": false }
  ]
}
```

**3. The release build**, in `.github/workflows/release.yml`. A managed plugin with no
`RuntimeIdentifier` is portable MSIL, so it needs ONE build, not a matrix entry — six RIDs would
upload six identical files. Build rather than publish (`Private="false"` keeps `CxAgent.Core` out of
the output, and a publish would pull in its whole dependency tree), then zip the DLL with its
sidecar and its `README.md`. The DLL and sidecar must ship together: the sidecar is read before the
assembly is loaded, so a zip missing it produces a plugin that cannot load and cannot be described.
The README is optional at the source but ships whenever present, so the plugin manager can render a
plugin's own documentation from disk.

**4. Nothing in the installer.** `install.sh` installs cxagent and no plugin at all. A plugin
reaches a machine through the manager, which reads the catalog, verifies the checksum and unpacks —
so a catalog entry is the whole of "shipping" it, and one route means one set of behaviours to get
right rather than two that can disagree.

**5. Nothing else.** In particular, do not add a config entry anywhere. An installer that enabled a
plugin would be enabling code the user was never asked about; cxagent reports it as
present-but-unconfigured and they decide.

> The plugin's name appears in the workflow and the installer by hand, in about six places. That is
> fine for a catalog this size and is the first thing worth generating from `plugins.json` if it
> grows.
