#!/bin/bash
# cxagent Installer
# Downloads and installs the latest release from GitHub
# Usage: curl -fsSL https://raw.githubusercontent.com/nickprotop/cxagent/master/install.sh | bash
# Copyright (c) Nikolaos Protopapas. All rights reserved.
# Licensed under the MIT License.

set -e

REPO="nickprotop/cxagent"
INSTALL_DIR="$HOME/.local/bin"

echo "Installing cxagent..."

# Detect OS and architecture
OS=$(uname -s)
ARCH=$(uname -m)

case "$OS" in
    Linux)
        case "$ARCH" in
            x86_64)  BINARY="cxagent-linux-x64" ;;
            aarch64) BINARY="cxagent-linux-arm64" ;;
            *) echo "Error: Unsupported Linux architecture: $ARCH"; exit 1 ;;
        esac
        ;;
    Darwin)
        case "$ARCH" in
            x86_64)  BINARY="cxagent-osx-x64" ;;
            arm64)   BINARY="cxagent-osx-arm64" ;;
            *) echo "Error: Unsupported macOS architecture: $ARCH"; exit 1 ;;
        esac
        ;;
    *)
        echo "Error: Unsupported OS: $OS"
        echo "cxagent supports Linux and macOS. For Windows, download from GitHub Releases."
        exit 1
        ;;
esac

# Get latest release info
echo "Fetching latest release..."
RELEASE_INFO=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest")
TAG=$(echo "$RELEASE_INFO" | grep '"tag_name"' | head -1 | sed 's/.*"tag_name": "\(.*\)".*/\1/')
VERSION="${TAG#v}"

if [ -z "$TAG" ]; then
    echo "Error: Could not determine latest release."
    exit 1
fi

echo "Latest version: $VERSION"

# Download binary
DOWNLOAD_URL="https://github.com/$REPO/releases/download/$TAG/$BINARY"
echo "Downloading $BINARY..."

mkdir -p "$INSTALL_DIR"
curl -fsSL "$DOWNLOAD_URL" -o "$INSTALL_DIR/cxagent"
chmod +x "$INSTALL_DIR/cxagent"

# Download uninstaller FROM THE RELEASE, not from master. The binary above is pinned to $TAG, so
# fetching its uninstaller from whatever master happens to be pairs a released binary with an
# unreleased script — a skew that only shows up when the two disagree, which is exactly when the
# uninstaller matters. Falls back to master for releases published before the scripts were attached
# as assets.
if ! curl -fsSL "https://github.com/$REPO/releases/download/$TAG/uninstall.sh" -o "$INSTALL_DIR/cxagent-uninstall.sh" 2>/dev/null; then
    curl -fsSL "https://raw.githubusercontent.com/$REPO/master/uninstall.sh" -o "$INSTALL_DIR/cxagent-uninstall.sh"
fi
chmod +x "$INSTALL_DIR/cxagent-uninstall.sh"

# THE csharp-lsp PLUGIN, INSTALLED BUT NOT CONFIGURED. cxagent reads config.json at startup and only
# loads plugins named there, so dropping this in the plugins folder gives the user tools they can
# TRY without this script deciding for them: cxagent announces it as present-but-unconfigured, and
# `/plugin load csharp-lsp.dll` or a config entry turns it on.
#
# NOT WRITING CONFIG IS THE POINT. An installer that enabled a plugin would be enabling code the
# user has not been asked about, and the load prompt — which shows a hash of the plugin's contents —
# is the one place that question belongs.
#
# FROM THE RELEASE, PINNED TO $TAG, for the same reason the uninstaller above is: a plugin built
# from a later commit than the binary it plugs into is a skew that surfaces as a puzzling failure.
#
# BEST EFFORT. A release predating the plugin has no such asset, and a failed optional download must
# not fail an otherwise good install of cxagent itself.
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/cxagent"
PLUGIN_DIR="$CONFIG_DIR/plugins"

if curl -fsSL "https://github.com/$REPO/releases/download/$TAG/csharp-lsp.zip" -o "/tmp/csharp-lsp-$$.zip" 2>/dev/null; then
    # VERIFIED AGAINST THE RELEASE'S OWN SHA256SUMS. The release computes these from the artifacts
    # it built, so a zip that does not match is one that changed between being built and being
    # downloaded — the case worth refusing rather than unpacking into a folder cxagent loads from.
    #
    # A MISSING SHA256SUMS IS NOT A FAILURE: releases predating it exist, and refusing to install
    # over an absent file would break them. What must never happen is unpacking a zip whose hash was
    # available and DID NOT match — that is the branch below.
    if curl -fsSL "https://github.com/$REPO/releases/download/$TAG/SHA256SUMS" -o "/tmp/csharp-lsp-sums-$$" 2>/dev/null        && command -v sha256sum > /dev/null 2>&1; then
        EXPECTED=$(grep " csharp-lsp.zip$" "/tmp/csharp-lsp-sums-$$" 2>/dev/null | awk '{print $1}')
        ACTUAL=$(sha256sum "/tmp/csharp-lsp-$$.zip" | awk '{print $1}')
        if [ -n "$EXPECTED" ] && [ "$EXPECTED" != "$ACTUAL" ]; then
            echo "  ! csharp-lsp.zip failed its checksum — not installing the plugin."
            rm -f "/tmp/csharp-lsp-$$.zip" "/tmp/csharp-lsp-sums-$$"
            PLUGIN_CHECKSUM_FAILED=1
        fi
    fi
    rm -f "/tmp/csharp-lsp-sums-$$"
fi

if [ -z "$PLUGIN_CHECKSUM_FAILED" ] && [ -f "/tmp/csharp-lsp-$$.zip" ]; then
    # 0700, MATCHING WHAT cxagent ITSELF CREATES. The config directory holds config.json with API
    # keys in it; creating a subdirectory of it under the caller's umask (commonly 0002 -> 0775)
    # would leave it group- and world-traversable.
    mkdir -p "$PLUGIN_DIR"
    chmod 700 "$CONFIG_DIR" "$PLUGIN_DIR" 2>/dev/null || true

    if command -v unzip > /dev/null 2>&1; then
        unzip -oq "/tmp/csharp-lsp-$$.zip" -d "$PLUGIN_DIR" && PLUGIN_INSTALLED=1
    fi
    rm -f "/tmp/csharp-lsp-$$.zip"
fi

# Ensure PATH
if [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
    SHELL_RC=""
    if [ -f "$HOME/.zshrc" ]; then
        SHELL_RC="$HOME/.zshrc"
    elif [ -f "$HOME/.bashrc" ]; then
        SHELL_RC="$HOME/.bashrc"
    fi

    if [ -n "$SHELL_RC" ]; then
        if ! grep -q "$INSTALL_DIR" "$SHELL_RC" 2>/dev/null; then
            echo "export PATH=\"$INSTALL_DIR:\$PATH\"" >> "$SHELL_RC"
            echo "Added $INSTALL_DIR to PATH in $SHELL_RC"
        fi
    fi
fi

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  ✓ cxagent v$VERSION installed!"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "  Binary:  $INSTALL_DIR/cxagent"
echo ""
echo "  Run:     cxagent"
echo "  Remove:  cxagent-uninstall.sh"
echo ""
if [ -n "$PLUGIN_INSTALLED" ]; then
    echo "  Also installed: the csharp-lsp plugin (C# code navigation)."
    echo "  It is NOT enabled — cxagent will say so at startup and tell you how."
    echo "  It needs a C# language server: csharp-ls (dotnet tool install -g csharp-ls)"
    echo ""
fi
if [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
    echo "  Note: Restart your shell or run:"
    echo "    source ~/.bashrc  (or ~/.zshrc)"
fi
