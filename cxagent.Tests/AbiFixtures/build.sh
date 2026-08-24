#!/usr/bin/env bash
# Compiles fixture_plugin.c into the small family of native .so files AbiPluginHostTests loads
# against a real cxagent-plugin-host process. Run from the MSBuild target in cxagent.Tests.csproj,
# not by hand — $1 is the output directory (the test project's own output dir), so the fixtures
# land next to cxagent.Tests.dll where the tests expect to find them.
#
# LINUX ONLY, DELIBERATELY. This host process and its native-loader path (NativeLibrary.Load, .so
# resolution) work the same on every platform dotnet targets, but building the ONE fixture library
# these tests load is a toolchain concern this script does not try to generalise across cc/clang/
# MSVC and .so/.dylib/.dll in one pass. A CI or contributor running elsewhere skips these tests
# rather than failing a build over a fixture unrelated to their platform's own correctness — see
# AbiPluginHostTests' own Skip.

set -euo pipefail

OUT_DIR="$1"
SRC="$(dirname "$0")/fixture_plugin.c"

if ! command -v cc >/dev/null 2>&1 && ! command -v gcc >/dev/null 2>&1; then
    echo "no C compiler (cc/gcc) found — skipping native ABI fixture build" >&2
    exit 0
fi
CC="$(command -v cc || command -v gcc)"

mkdir -p "$OUT_DIR"

build() {
    local name="$1"
    shift
    "$CC" -shared -fPIC -o "$OUT_DIR/$name.so" "$@" "$SRC"
}

build fixture-wellformed
build fixture-malformed -DFIXTURE_MALFORMED
build fixture-crash -DFIXTURE_CRASH
build fixture-badversion -DFIXTURE_BADVERSION
build fixture-noinvoke -DFIXTURE_NOINVOKE
