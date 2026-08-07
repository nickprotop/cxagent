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

# Clean PATH from shell config
for RC in "$HOME/.bashrc" "$HOME/.zshrc"; do
    if [ -f "$RC" ] && grep -q "$INSTALL_DIR" "$RC" 2>/dev/null; then
        sed -i "\|$INSTALL_DIR|d" "$RC"
        echo ""
        echo "✓ Removed PATH entry from $RC"
    fi
done

echo ""
echo "✓ cxagent uninstalled."
