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
if [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
    echo "  Note: Restart your shell or run:"
    echo "    source ~/.bashrc  (or ~/.zshrc)"
fi
