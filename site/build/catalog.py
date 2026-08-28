#!/usr/bin/env python3
"""Turn the committed catalog into the published one.

THE HASH CANNOT LIVE IN THE COMMITTED FILE. plugins.json is committed before a release builds the
zip it describes, so it cannot contain that zip's hash -- see the design doc, "The problem". This
reads the committed catalog, hashes the artifact a release actually shipped, and writes a copy
carrying the value. Nothing is committed: the output goes into a Pages artifact, which never
enters git.

STANDARD LIBRARY ONLY. This runs in CI with no pip install step, and adding one would make a
release depend on an index being reachable.
"""

import argparse
import hashlib
import json
import sys
from collections import OrderedDict
from pathlib import Path


def sha256_of(path: Path) -> str:
    """Streamed rather than read whole: the artifact is a zip of unbounded size."""
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=Path,
                       help="the committed plugins.json")
    parser.add_argument("--out", required=True, type=Path,
                       help="where to write the published catalog")
    parser.add_argument("--plugin", action="append", default=[], metavar="NAME=PATH",
                       help="a catalog entry and the release artifact whose hash fills its "
                            "source.sha256; repeat for each plugin")
    parser.add_argument("--version", metavar="X.Y.Z",
                       help="cxagent's own version, published as cxagentVersion for the site to "
                            "show; omitted locally, where there is no release to name")
    args = parser.parse_args()

    # EVERY PLUGIN IN ONE PASS. Chaining invocations would mean each reading the previous one's
    # output, so a failure partway leaves a half-stamped catalog already written — and this script
    # exists to make that state impossible.
    assets = {}
    for pair in args.plugin:
        name, _, path = pair.partition("=")
        if not name or not path:
            print(f"error: --plugin wants NAME=PATH, got '{pair}'.", file=sys.stderr)
            return 1
        assets[name] = Path(path)

    if not assets:
        print("error: no --plugin given; nothing to stamp.", file=sys.stderr)
        return 1

    # LOUD, NOT NULL. A catalog published with sha256 still null looks exactly like the committed
    # file, so the one thing this script exists to add would be missing with nothing to show for it.
    for name, path in assets.items():
        if not path.is_file():
            print(f"error: no artifact at '{path}' for '{name}' -- refusing to publish a catalog "
                  f"without the hash it exists to carry.", file=sys.stderr)
            return 1

    catalog = json.loads(args.catalog.read_text(), object_pairs_hook=OrderedDict)

    # THE MAINTAINER COMMENT DOES NOT SHIP. It is 60 lines telling whoever edits the source file
    # how to edit it, which a client fetching this over the network pays for on every request and
    # can do nothing with.
    catalog.pop("$comment", None)

    # A url ENTRY IS ALREADY PINNED. Stamping one would replace a hash the maintainer committed
    # deliberately -- pinning a file this project does not control -- with the hash of whatever CI
    # happened to be handed.
    for entry in catalog.get("plugins", []):
        name = entry.get("name")
        if name in assets and (entry.get("source") or {}).get("kind") != "release":
            print(f"error: '{name}' is not a 'release' entry; its sha256 is committed and must not "
                  f"be overwritten.", file=sys.stderr)
            return 1

    stamped = set()
    for entry in catalog.get("plugins", []):
        name = entry.get("name")
        if name in assets:
            digest = sha256_of(assets[name])
            entry.setdefault("source", OrderedDict())["sha256"] = digest
            stamped.add(name)
            print(f"{name}: {digest}")

    missing = set(assets) - stamped
    if missing:
        print(f"error: no catalog entry named {', '.join(sorted(missing))}.", file=sys.stderr)
        return 1

    # EVERY ENTRY LEAVES WITH A HASH, however it got one. An entry still null here is one a client
    # cannot verify, which is the whole reason this script exists — but the two source kinds get
    # there differently and only one of them is ours to stamp.
    # AN ENTRY USING PER-RID `sources` HAS NO SINGLE `source.sha256` and is not unstamped — it
    # carries a hash per platform. No such entry exists yet; the first ABI plugin would otherwise
    # trip this check the day it is added.
    unstamped = [e.get("name") for e in catalog.get("plugins", [])
                 if "sources" not in e and (e.get("source") or {}).get("sha256") is None]
    if unstamped:
        print(f"error: {', '.join(unstamped)} would publish with a null sha256. A 'release' entry "
              f"needs --plugin NAME=PATH; a 'url' entry must carry its hash in plugins.json.",
              file=sys.stderr)
        return 1

    # CXAGENT'S OWN VERSION, WHICH IS NOT A PLUGIN'S. A plugin that did not change keeps its number
    # across releases, so plugins[0].version answers "which csharp-lsp is this?" and never "which
    # cxagent is this?" — the two agreed only until the first release that carried a plugin forward.
    # Published as its own key so a reader cannot mistake one for the other.
    if args.version:
        catalog["cxagentVersion"] = args.version

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(catalog, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
