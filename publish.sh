#!/bin/bash
# cxagent Release Publisher
# Bumps version and creates a new release tag that triggers GitHub Actions
# Copyright (c) Nikolaos Protopapas. All rights reserved.
# Licensed under the MIT License.

set -e

BUMP_TYPE="patch"
FORCE=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --force|-f) FORCE=true; shift ;;
        major|minor|patch) BUMP_TYPE="$1"; shift ;;
        *)
            echo "Usage: $0 [major|minor|patch] [--force]"
            echo "  $0              # Bump patch (default)"
            echo "  $0 minor        # Bump minor (0.1.0 -> 0.2.0)"
            echo "  $0 major        # Bump major (0.1.0 -> 1.0.0)"
            exit 1 ;;
    esac
done

# Pre-flight checks
if ! git diff-index --quiet HEAD --; then
    echo "Error: Uncommitted changes. Commit or stash first."
    git status --short
    exit 1
fi

CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
UPSTREAM=$(git rev-parse --abbrev-ref --symbolic-full-name @{u} 2>/dev/null || echo "")

if [ -n "$UPSTREAM" ]; then
    LOCAL=$(git rev-parse HEAD)
    REMOTE=$(git rev-parse "$UPSTREAM")
    if [ "$LOCAL" != "$REMOTE" ]; then
        UNPUSHED=$(git log "$UPSTREAM..HEAD" --oneline 2>/dev/null | wc -l)
        if [ "$UNPUSHED" -gt 0 ]; then
            echo "Error: $UNPUSHED unpushed commit(s). Push first."
            git log "$UPSTREAM..HEAD" --oneline
            exit 1
        fi
    fi
fi

# Parse current version
LATEST_TAG=$(git describe --tags --abbrev=0 2>/dev/null || echo "v0.0.0")
VERSION="${LATEST_TAG#v}"
IFS='.' read -r MAJOR MINOR PATCH <<< "$VERSION"

# Bump
case "$BUMP_TYPE" in
    major) MAJOR=$((MAJOR + 1)); MINOR=0; PATCH=0 ;;
    minor) MINOR=$((MINOR + 1)); PATCH=0 ;;
    patch) PATCH=$((PATCH + 1)) ;;
esac

NEW_VERSION="$MAJOR.$MINOR.$PATCH"
NEW_TAG="v$NEW_VERSION"

# THE PLUGIN'S VERSION, DECIDED HERE AND NOT IN CI. A plugin that changed takes this tag's version;
# one that did not keeps what it has, so the number keeps meaning "the plugin's contract" rather
# than counting cxagent releases. Seeing 0.9.0 on a v0.12.0 release is then informative.
#
# BEFORE THE TAG, WHICH IS THE POINT OF DOING IT HERE. Written after the tag — as CI would have to —
# the tag itself would contain the old version, and anyone checking out v0.9.0 would find a plugin
# claiming to be something else.
LSP_DIR="plugins/csharp-lsp"
LSP_SIDECAR="$LSP_DIR/csharp-lsp.plugin.json"
LSP_VERSION=$(python3 -c "import json;print(json.load(open('$LSP_SIDECAR'))['version'])")
LSP_CHANGED=false

if [ -z "$LATEST_TAG" ] || [ "$LATEST_TAG" = "v0.0.0" ] \
   || ! git diff --quiet "$LATEST_TAG" HEAD -- "$LSP_DIR"; then
    LSP_CHANGED=true
fi

CALC_DIR="plugins/calculator"
CALC_SIDECAR="$CALC_DIR/calculator.plugin.json"
CALC_VERSION=$(python3 -c "import json;print(json.load(open('$CALC_SIDECAR'))['version'])")
CALC_CHANGED=false

if [ -z "$LATEST_TAG" ] || [ "$LATEST_TAG" = "v0.0.0" ] \
   || ! git diff --quiet "$LATEST_TAG" HEAD -- "$CALC_DIR"; then
    CALC_CHANGED=true
fi

# ONE FLAG FOR THE SHARED MACHINERY. The bump, the prompt and the commit each happen once however
# many plugins changed; which ones changed stays per-plugin above, so an untouched plugin is never
# bumped along for the ride.
ANY_PLUGIN_CHANGED=false
if [ "$LSP_CHANGED" = true ] || [ "$CALC_CHANGED" = true ]; then
    ANY_PLUGIN_CHANGED=true
fi

# THE BUMP HAPPENS BEFORE THE TESTS, so the suite validates exactly what will be pushed.
# PluginCatalogTests pins the catalog entry to the sidecar: writing one without the other is a
# failing build, and that is only useful if the tests run AFTER the write. Nothing is committed
# yet — a failure here leaves an edited working tree and no history to unpick.
if [ "$ANY_PLUGIN_CHANGED" = true ]; then
    python3 - "$NEW_VERSION" "$LSP_CHANGED" "$CALC_CHANGED" <<'PYBUMP'
import json, sys, collections

version, lsp_changed, calc_changed = sys.argv[1:4]

# BOTH FILES, ALWAYS TOGETHER — see the note above on why the tests are the check for this. Only
# the plugins that changed are bumped: an untouched one keeps its number, which is what keeps the
# number meaning the plugin's contract.
changed = []
if lsp_changed == "true":
    changed.append(("csharp-lsp", "plugins/csharp-lsp/csharp-lsp.plugin.json"))
if calc_changed == "true":
    changed.append(("calculator", "plugins/calculator/calculator.plugin.json"))

for name, sidecar_path in changed:
    sidecar = json.load(open(sidecar_path), object_pairs_hook=collections.OrderedDict)
    sidecar["version"] = version
    json.dump(sidecar, open(sidecar_path, "w"), indent=2)
    open(sidecar_path, "a").write("\n")

names = {name for name, _ in changed}
catalog_path = "plugins/plugins.json"
catalog = json.load(open(catalog_path), object_pairs_hook=collections.OrderedDict)
for entry in catalog["plugins"]:
    if entry.get("name") in names:
        entry["version"] = version
json.dump(catalog, open(catalog_path, "w"), indent=2)
open(catalog_path, "a").write("\n")
PYBUMP
fi

# THE TESTS GATE THE TAG, not just the build. CI runs them before packing, but by then the tag
# exists — and a tag that publishes to NuGet spends a version number permanently, because NuGet
# allows unlisting and never reuse. Failing here costs a minute; failing there costs the number.
echo ""
echo "Running tests..."
if ! dotnet test --configuration Release --verbosity quiet > /tmp/cxagent-release-test.log 2>&1; then
    echo "Error: tests failed. Not tagging."
    tail -20 /tmp/cxagent-release-test.log
    exit 1
fi
grep -E "Passed!|Failed!" /tmp/cxagent-release-test.log || true

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  cxagent Release: $NEW_TAG"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  Previous: $LATEST_TAG"
echo "  New:      $NEW_TAG ($BUMP_TYPE)"
echo "  Plugins:"
echo "    csharp-lsp $LSP_VERSION$([ "$LSP_CHANGED" = true ] && echo " -> $NEW_VERSION (changed)" || echo " (unchanged, carried forward)")"
echo "    calculator $CALC_VERSION$([ "$CALC_CHANGED" = true ] && echo " -> $NEW_VERSION (changed)" || echo " (unchanged, carried forward)")"
echo ""
echo "  This tag publishes TWO things:"
echo "    · GitHub release — six platform binaries, revocable"
echo "    · CxAgent.Core $NEW_VERSION to nuget.org — PERMANENT, the version"
echo "      can be unlisted but never reused"
echo ""

if [ "$FORCE" = false ]; then
    # THE PROMPT NAMES THE COMMIT TOO. Answering yes pushes a version bump to master before the
    # tag is created, and a question that only mentions tagging hides that.
    if [ "$ANY_PLUGIN_CHANGED" = true ]; then
        read -p "Commit the changed plugin versions to $NEW_VERSION, then create and push tag '$NEW_TAG'? [y/N] " -n 1 -r
    else
        read -p "Create and push tag '$NEW_TAG'? [y/N] " -n 1 -r
    fi
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Aborted."
        # THE BUMP IS ALREADY WRITTEN, uncommitted, because the tests had to see it. Leaving that
        # unexplained means the next run refuses over a dirty tree with no idea why.
        if [ "$ANY_PLUGIN_CHANGED" = true ] && ! git diff --quiet plugins/; then
            echo ""
            echo "  Note: the plugin version bump to $NEW_VERSION is in your working tree, uncommitted."
            echo "  Undo it with:  git checkout -- plugins/"
        fi
        exit 0
    fi
fi

if [ "$ANY_PLUGIN_CHANGED" = true ] && ! git diff --quiet plugins/; then
    git add plugins/csharp-lsp/csharp-lsp.plugin.json plugins/calculator/calculator.plugin.json plugins/plugins.json
    git commit -m "Set changed plugin versions to $NEW_VERSION"
    git push origin HEAD
    echo "  ✓ changed plugin versions set to $NEW_VERSION and pushed"
    BUMP_PUSHED=true
fi

# THE TAG, AND A CLEAR ACCOUNT IF IT FAILS. `set -e` would otherwise exit silently with the bump
# already on master and no release to go with it — a state that is recoverable but impossible to
# diagnose from the outside. The trap fires only on the tag steps, and only when there is something
# pushed to explain.
tag_failed() {
    echo ""
    echo "Error: tagging failed AFTER the version bump was pushed."
    echo "  master now has the changed plugins at $NEW_VERSION, and $NEW_TAG does not exist."
    echo "  Re-run this script: it will see the plugins as unchanged, keep $NEW_VERSION,"
    echo "  and tag it — which is the state you wanted."
}
if [ "${BUMP_PUSHED:-false}" = true ]; then
    trap tag_failed ERR
fi

git tag -a "$NEW_TAG" -m "Release $NEW_TAG"
git push origin "$NEW_TAG"
trap - ERR

echo ""
echo "✓ Release $NEW_TAG published!"
echo ""
echo "GitHub Actions will build and create the release:"
echo "  https://github.com/nickprotop/cxagent/actions"
echo ""
echo "Release will be at:"
echo "  https://github.com/nickprotop/cxagent/releases/tag/$NEW_TAG"
echo ""
echo "And the package at:"
echo "  https://www.nuget.org/packages/CxAgent.Core/$NEW_VERSION"
echo ""
echo "The nuget job runs LAST and only if the release job succeeded, so a failure"
echo "there leaves the binaries shipped and the version unspent — fix and re-tag."
