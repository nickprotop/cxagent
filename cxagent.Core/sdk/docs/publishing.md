# Publishing a plugin

*From a plugin that passes `check` to an entry in the catalog every cxagent user can install from.*

The catalog is [`plugins/plugins.json`](https://github.com/nickprotop/cxagent/blob/master/plugins/plugins.json). Each entry says where a plugin's
zip is, its sha256, and what it claims to be: name, version, tools and their gates, contract,
platforms. cxagent reads it to offer plugins in `/plugin`. Your zips stay where you put them — the
catalog only points at them.

## 1. Pack

```
cxagent plugin pack <folder> --out dist                                   # C#
cxagent plugin pack build --native build/wordcount.so=linux-x64 --out dist  # C, one --native per platform
```

`pack` runs `check` first and refuses a plugin with any problem. It writes one zip (one per
platform for native) and `<name>.catalog.json`, with each URL left as `<upload URL>`. The text fields
come from your csproj or `--info catalog-info.yaml`; a field still `<fill in: …>` is refused later.

## 2. Upload

Put the zips somewhere they stay, at an `https://` URL. A GitHub release in your repository is the
usual place:

```
gh release create v1.0.0 dist/*.zip
```

Then replace each `<upload URL>` in `<name>.catalog.json` with that zip's URL.

## 3. Verify what a user would install

```
cxagent plugin verify --https-only dist/<name>.catalog.json
```

It downloads each zip, checks the sha256, unpacks it and holds the sidecar and files up against every
field the entry claims. No plugin code runs. `--https-only` refuses a local path, as the bot does:
leave it off to check an entry pointing at zips on disk before you upload.

## 4. Submit

**Through the issue** (recommended): open
[Submit a plugin](https://github.com/nickprotop/cxagent/issues/new/choose) and paste
`<name>.catalog.json`. A bot then:

- runs the same `verify --https-only`, and comments the result — each problem on its own line;
- when it passes, opens a pull request adding the entry, linked to your issue;
- on each edit of the issue, checks again: a passing edit updates the pull request, a failing one
  closes it until a later edit passes.

A maintainer reviews the pull request and merges it; merging closes your issue. Closing the issue
yourself withdraws the submission and closes its pull request. Edits to a closed issue do nothing;
reopen it to submit again.

**Or as a pull request** adding your entry to `plugins/plugins.json`. CI runs
`verify --https-only` on every third-party entry in the file. That is all it checks: the name and
version rules below are the reviewer's to apply on this path, and a first-time contributor's CI
waits for a maintainer to approve it. The issue is the quicker way.

## Names and updates

| Your entry's name | Already in the catalog | Result |
|---|---|---|
| new | no | added |
| a plugin this repository builds | yes | refused: choose another name |
| yours | from the same `repository` | an update, if its version is higher (`1.10.0` beats `1.9.0`) |
| someone else's | from another `repository` | refused; a maintainer can decide otherwise on the issue |

An update replaces your entry in place. Its version must be higher: cxagent offers an update whenever
the version differs, so an equal or lower one would be offered as an update that changes nothing.

One submission per name at a time: while one issue's pull request for a name waits for review,
another issue submitting the same name is refused, naming that pull request.

## The entry's shape

`pack` writes it, and verify holds a hand-edited one to the same shape: a `source` (or, for a native
plugin, one `sources` entry per platform) with `"kind": "url"` and a `"url"`, platforms from
`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` and `win-arm64`, and every value of the
type pack writes.

**A native plugin is per-platform, and the entry has to say which.** A managed plugin is portable
MSIL — one build runs everywhere cxagent does, so it says `platforms: ["any"]` and has one `source`.
A native plugin is a library per platform: a `.so` does not load on Windows, and you may ship
`linux-x64` and nothing else. Its entry names the platforms it actually has and keys its downloads by
the same names under `sources`, each with its own `sha256` and, where the name differs per platform,
its own `file`. The installer takes this machine's build, and says "not available for this machine"
when there is none rather than downloading a library that cannot load. `publisher`, `license` and
`repository` matter for any plugin cxagent does not build itself: they are how a user knows who wrote
it.

### Two worked entries

Neither is real, and neither belongs in `plugins.json`, which carries only plugins that exist. Copy
the one whose shape matches what you are publishing; `pack` writes the same shape for you.

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
    "pluginContract": 4,
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
    "pluginContract": 4,
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

## What review checks

Whether the entry is truthful and useful: that the plugin exists, its entry matches its sidecar, and
its description is honest. It is not a security review. cxagent asks before loading any plugin,
showing a hash of everything it would load, and that is the boundary a user relies on.

## Running your own store

A store is a `catalog.json` and the zips it points to, served from any static host. `cxagent store build`
makes both from a folder of built plugins, one plugin per subfolder:

```
plugins/
  weather/
    weather.plugin.json
    weather.dll
    pack-info.json        description, publisher, license, repository
  grep-tool/
    grep-tool.plugin.json
    libgrep-tool.so
    linux-arm64/grep-tool.so   a native plugin's other platforms: <rid>/<library>
```

```
cxagent store build plugins --url https://tools.example/cx --out site
```

`--url` is the address `site` is served from: https, or `http://localhost` for testing. The command
checks every plugin as `plugin check` does, packs it as `plugin pack` does, and writes `site/catalog.json`
with each download addressed `https://tools.example/cx/<zip>`, beside one zip per plugin (per platform,
for a native one). It prints each zip's hash, then the line to give your users.

Each plugin's catalog text comes from the `pack-info.json` (or `.yaml`) in its folder, else its project.
If a plugin does not load, a field is still unfilled, two plugins share a name, or the catalog would
fail `plugin verify`'s shape rules, nothing is written and each problem is listed under its folder's name.

Exit codes: 0 built, 1 a plugin or the folder is at fault, 2 usage.

Upload `site` so `catalog.json` is at `<base-url>/catalog.json`. Users then run
`/plugin store add <name> <base-url>/catalog.json`, and the store's plugins appear in the plugin manager
and install with `/plugin get <name>/<plugin>`.

## First-party plugins

*For maintainers: a plugin that lives in this repository is built and released with cxagent itself, so
it skips the upload and the submission above, and carries the same expectations as the app.*

**1. The directory.** `plugins/<name>/`, with the project, its sidecar, and a `README.md` covering
what the plugin does, anything the user must install separately, and every setting. Follow
[csharp-lsp](../../../plugins/csharp-lsp/README.md).

**2. The catalog entry**, in [`plugins.json`](../../../plugins/plugins.json). `name`, `version`,
`spawns`, `tools` and `pluginContract` must match the plugin's own sidecar — `PluginCatalogTests` fails
the build if they drift, because the plugin manager shows what the catalog says without loading the
plugin to check. `tools` carries each tool's `gated` value as well as its name, so the manager can say
which tools ask before acting.

A plugin released here uses `"kind": "release"` and carries `latest`, GitHub's always-current alias,
which resolves to the newest release carrying the asset. Commit its `sha256` as `null`: the site
build stamps the hash of the zip the release actually carries into the published catalog, and
refuses to publish without it. The installer checks every download against that hash. An entry of
`"kind": "url"` keeps its own committed `sha256` — nothing in this project can vouch for a file it does
not build, so the site build never overwrites it.

**3. The release build**, in `.github/workflows/release.yml`. A managed plugin with no
`RuntimeIdentifier` is portable MSIL, so it needs ONE build, not a matrix entry — six RIDs would
upload six identical files. Build rather than publish (`Private="false"` keeps `CxAgent.Core` out of
the output, and a publish would pull in its whole dependency tree), then zip the DLL with its
sidecar and its `README.md`. The DLL and sidecar must ship together: the sidecar is read before the
assembly is loaded, so a zip missing it produces a plugin that cannot load and cannot be described.
The README ships whenever present, so the plugin manager can render a plugin's own documentation
from disk.

**4. The site build**, in `.github/workflows/pages.yml`, names each first-party plugin twice: once
to download its zip from the release, once to stamp its hash into the published catalog. Skip it and
the catalog build stops with "would publish with a null sha256" — loud, but a plugin added to
`release.yml` alone does not reach the catalog.

**5. Nothing in the installer, and nothing in config.** `install.sh` installs cxagent and no plugin:
a plugin reaches a machine through the plugin manager, which reads the catalog, verifies the
checksum and unpacks — one route, one set of behaviours to get right. And no config entry anywhere:
an installer that enabled a plugin would be enabling code the user was never asked about. cxagent
reports it as present-but-unconfigured, and they decide.

> The plugin's name appears in `release.yml` and `pages.yml` by hand, in about six places. That is
> fine for a catalog this size, and is the first thing worth generating from `plugins.json` if it
> grows.
