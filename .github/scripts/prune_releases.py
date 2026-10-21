#!/usr/bin/env python3
"""Delete every GitHub release but the newest few, by version.

KEEPS THE TAGS. `gh release delete` without --cleanup-tag removes the release and its assets and
leaves the git tag: a version stays checkoutable and buildable, and NuGet, which never reads a
GitHub release, keeps serving it.

BY VERSION, NOT BY DATE OR BY TEXT. A re-run of an old release's workflow publishes it again with a
new date, and "v0.9.9" sorts after "v0.9.10" as text; neither is "older".

ONLY vX.Y.Z. A release tagged any other way -- a pre-release, a one-off -- was named by someone for a
reason this script cannot know, so it is never touched.

STANDARD LIBRARY ONLY, like catalog.py: a release must not depend on a package index.
"""

import argparse
import re
import subprocess
import sys
from pathlib import Path

VERSION = re.compile(r"v(\d+)\.(\d+)\.(\d+)")


def version_of(tag):
    match = VERSION.fullmatch(tag)
    return tuple(int(part) for part in match.groups()) if match else None


def plan(tags, keep, current):
    """(kept, deleted), newest first. The tag this run is for is always kept."""
    versioned = sorted((t for t in tags if version_of(t)), key=version_of, reverse=True)
    kept = versioned[:keep]
    # THE GUARD: a re-run for an older tag must not delete the release it just published.
    if current in versioned and current not in kept:
        kept.append(current)
    deleted = [t for t in versioned if t not in kept]
    return kept, deleted


def gh(*args):
    return subprocess.run(["gh", *args], check=True, capture_output=True, text=True).stdout


def main(argv=None, run=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, help="OWNER/NAME")
    parser.add_argument("--current", required=True, help="the tag this release run is for")
    parser.add_argument("--keep", type=int, default=3)
    parser.add_argument("--dry-run", action="store_true", help="list what would be kept and deleted; delete nothing")
    parser.add_argument("--tags-from", type=Path, help="read tags from this file instead of asking GitHub")
    args = parser.parse_args(argv)
    run = run or (lambda tag: gh("release", "delete", tag, "--repo", args.repo, "--yes"))

    if args.tags_from:
        tags = args.tags_from.read_text().split()
    else:
        # --limit: gh lists 30 by default, and a release beyond the 30th would never be pruned.
        tags = gh("release", "list", "--repo", args.repo, "--limit", "1000",
                  "--json", "tagName", "-q", ".[].tagName").split()

    kept, deleted = plan(tags, args.keep, args.current)
    # LOGGED BEFORE ACTING, so a run that fails partway still shows what it meant to do.
    print("keeping: " + (", ".join(kept) or "(none)"))
    print("deleting: " + (", ".join(deleted) or "(none)"))
    if args.dry_run:
        return 0
    for tag in deleted:
        run(tag)
        print(f"deleted the {tag} release; its tag stays")
    return 0


if __name__ == "__main__":
    sys.exit(main())
