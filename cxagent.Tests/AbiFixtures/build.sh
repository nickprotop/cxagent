#!/usr/bin/env bash
# Compiles fixture_plugin.c into the small family of native libraries AbiPluginHostTests loads
# against a real cxagent-plugin-host process. Run from the MSBuild target in cxagent.Tests.csproj,
# not by hand — $1 is the output directory (the test project's own output dir), so the fixtures
# land next to cxagent.Tests.dll where the tests expect to find them.
#
# THE FALLBACK FOR LINUX AND MACOS WITHOUT CMAKE — AbiFixtures/CMakeLists.txt is the build
# everywhere cmake is installed, Windows included. A machine with neither builds no fixtures, and
# the native tests skip rather than fail.

set -euo pipefail

OUT_DIR="$1"
SRC="$(dirname "$0")/fixture_plugin.c"
HEADER_DIR="$(dirname "$0")/../../cxagent.Core/sdk/c"

if ! command -v cc >/dev/null 2>&1 && ! command -v gcc >/dev/null 2>&1; then
    echo "no C compiler (cc/gcc) found — skipping native ABI fixture build" >&2
    exit 0
fi
CC="$(command -v cc || command -v gcc)"

# THE SUFFIX THE TESTS LOOK FOR (NativeFixtures.Suffix): a .so on macOS would be built and never
# found, and every native test would skip while reporting as passed.
SUFFIX=so
[ "$(uname -s)" = Darwin ] && SUFFIX=dylib

mkdir -p "$OUT_DIR"

build() {
    local name="$1"
    shift
    "$CC" -shared -fPIC -I"$HEADER_DIR" -o "$OUT_DIR/$name.$SUFFIX" "$@" "$SRC"
}

build fixture-wellformed
build fixture-malformed -DFIXTURE_MALFORMED
build fixture-crash -DFIXTURE_CRASH
build fixture-badversion -DFIXTURE_BADVERSION
build fixture-disagrees -DFIXTURE_DISAGREES
build fixture-noinvoke -DFIXTURE_NOINVOKE
build fixture-submits -DFIXTURE_SUBMITS
build fixture-commands -DFIXTURE_COMMANDS
build fixture-reports -DFIXTURE_REPORTS -pthread
