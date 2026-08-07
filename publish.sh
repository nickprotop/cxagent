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

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  cxagent Release: $NEW_TAG"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  Previous: $LATEST_TAG"
echo "  New:      $NEW_TAG ($BUMP_TYPE)"
echo ""

if [ "$FORCE" = false ]; then
    read -p "Create and push tag '$NEW_TAG'? [y/N] " -n 1 -r
    echo
    [[ ! $REPLY =~ ^[Yy]$ ]] && echo "Aborted." && exit 0
fi

git tag -a "$NEW_TAG" -m "Release $NEW_TAG"
git push origin "$NEW_TAG"

echo ""
echo "✓ Release $NEW_TAG published!"
echo ""
echo "GitHub Actions will build and create the release:"
echo "  https://github.com/nickprotop/cxagent/actions"
echo ""
echo "Release will be at:"
echo "  https://github.com/nickprotop/cxagent/releases/tag/$NEW_TAG"
