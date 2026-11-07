#!/bin/bash
# cxagent Uninstaller
# Removes cxagent binary
# Copyright (c) Nikolaos Protopapas. All rights reserved.
# Licensed under the MIT License.

INSTALL_DIR="$HOME/.local/bin"

echo "cxagent Uninstaller"
echo ""

# Remove binary
if [ -f "$INSTALL_DIR/cxagent" ]; then
    rm "$INSTALL_DIR/cxagent"
    echo "✓ Removed $INSTALL_DIR/cxagent"
else
    echo "  Binary not found at $INSTALL_DIR/cxagent"
fi

# Remove uninstaller
if [ -f "$INSTALL_DIR/cxagent-uninstall.sh" ]; then
    rm "$INSTALL_DIR/cxagent-uninstall.sh"
fi

# Remove the ABI plugin host, installed beside the binary by install.sh.
if [ -d "$INSTALL_DIR/plugin-host" ]; then
    rm -rf "$INSTALL_DIR/plugin-host"
    echo "✓ Removed $INSTALL_DIR/plugin-host"
fi

# Clean PATH from shell config: THE LINE install.sh WROTE, AND ONLY THAT ONE. Matched whole and as fixed text, so a
# line of the user's own that also names ~/.local/bin (pipx adds one) is kept. Rewritten through a temp file with
# POSIX grep, not `sed -i`, whose in-place flag differs between GNU and macOS; `cat >` keeps the file's own
# permissions, and a link stays a link.
LINE="export PATH=\"$INSTALL_DIR:\$PATH\""
for RC in "$HOME/.bashrc" "$HOME/.zshrc"; do
    if [ -f "$RC" ] && grep -qxF "$LINE" "$RC" 2>/dev/null; then
        TMP=$(mktemp) || continue
        grep -vxF "$LINE" "$RC" > "$TMP"
        cat "$TMP" > "$RC"
        rm -f "$TMP"
        echo ""
        echo "✓ Removed PATH entry from $RC"
    fi
done

echo ""
echo "✓ cxagent uninstalled."

# WHAT IS DELIBERATELY LEFT. The config directory holds config.json with API keys in it, session
# history and logs — deleting that on an uninstall would destroy work nobody asked to lose, and a
# reinstall would start from nothing. Any plugin installed through the manager lives inside that
# directory and is left for the same reason. Say so, because a user who installed a plugin
# reasonably expects uninstall to mention it.
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/cxagent"
if [ -d "$CONFIG_DIR" ]; then
    echo ""
    echo "  Left in place: $CONFIG_DIR"
    echo "  (config.json, history, logs$([ -d "$CONFIG_DIR/plugins" ] && echo ", plugins"))"
    echo "  Remove it yourself if you want a clean slate."
fi
