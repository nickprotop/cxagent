/*
 * A tiny native ABI plugin, compiled several ways by AbiFixtures.build.sh — each build's -D flag
 * selects a different misbehaviour, so AbiPluginHostTests can exercise the host process against a
 * REAL shared library rather than only at the managed AbiCodec seam. See that script for which
 * flag builds which .so.
 *
 * FIXTURE_WELLFORMED (default): answers describe/start/invoke/stop correctly — one tool, "echo",
 * that reflects its "value" argument back in the JobResult output.
 * FIXTURE_MALFORMED: cxagent_plugin_invoke returns JSON that is not a valid envelope.
 * FIXTURE_CRASH: cxagent_plugin_invoke dereferences a null pointer — a REAL segfault, to prove the
 * host survives a native crash rather than merely a well-behaved error path.
 * FIXTURE_BADVERSION: cxagent_plugin_abi_version reports a version this host does not understand.
 * FIXTURE_NOINVOKE: omits cxagent_plugin_invoke entirely — a library missing a required export.
 * FIXTURE_SUBMITS: exports cxagent_plugin_poll, which returns one submit
 * ({"goal":"run the tests","wantResult":false}) on its FIRST call and NULL on every call after —
 * once, not every tick, so a test waiting for exactly one submit line does not have to distinguish
 * "the host stopped polling" from "the plugin keeps sending the same one."
 * FIXTURE_COMMANDS: exports cxagent_plugin_command, one declared command "greet" that echoes its
 * argument back, so AbiPluginHostTests can prove the host dispatches a real command through a real
 * process rather than only at the managed AbiCodec seam. Every other build omits this export
 * entirely, proving NativePlugin.HasCommand is false and the load still succeeds without it.
 * FIXTURE_MALFORMED also counts cxagent_plugin_free calls to a file (see FREE_COUNT_PATH env var,
 * read once at process start) — AbiPluginHostTests.FreeIsCalledExactlyOnce_EvenOnAParseFailure
 * reads it back to prove the host's free-exactly-once discipline holds on the parse-failure path,
 * not just the happy path.
 */

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

/* Every returned string is heap-allocated so cxagent_plugin_free's free() is never asked to
 * release memory it did not itself allocate — cxagent_plugin.h's ownership rule, honoured on the
 * plugin side of the boundary this fixture stands in for. */
static char* dup_str(const char* s) {
    size_t len = strlen(s) + 1;
    char* copy = (char*)malloc(len);
    memcpy(copy, s, len);
    return copy;
}

int32_t cxagent_plugin_abi_version(void) {
#ifdef FIXTURE_BADVERSION
    return 99;
#else
    return 2;
#endif
}

const char* cxagent_plugin_describe(void) {
    return dup_str(
        "{\"pluginContract\":2,\"name\":\"fixture\",\"version\":\"1.0.0\",\"instructions\":null,"
        "\"spawns\":false,"
#ifdef FIXTURE_SUBMITS
        /* client:true — MUST MATCH THE SIDECAR (fixture-submits.plugin.json), which is what
         * AbiPluginLoader checks describe() against; without this, PluginManifestMatch.Mismatch
         * refuses the load before poll ever gets a chance to run. */
        "\"client\":true,"
#endif
        "\"tools\":[{\"name\":\"echo\",\"description\":\"echoes its argument\","
        "\"inputSchema\":{\"type\":\"object\"},\"gated\":false},"
        "{\"name\":\"echo_dynamic\",\"description\":\"echoes, asking about some arguments\","
        "\"inputSchema\":{\"type\":\"object\"},\"gated\":\"dynamic\"}]}");
}

/*
 * GATE. Returns NULL when this call needs no prompt, or a JSON object naming what to show. Every
 * v2 plugin exports this — one that gates nothing returns NULL unconditionally, which is cheaper
 * than a second export table for hosts to reason about.
 *
 * A PANIC MUST NOT CROSS THIS BOUNDARY (see cxagent_plugin.h): a gate that cannot decide returns
 * malformed output or NULL, and the host reads that as "ask", never as "allow".
 */
const char* cxagent_plugin_gate(const char* tool_name, const char* call_json) {
    if (tool_name == NULL || strcmp(tool_name, "echo_dynamic") != 0) return NULL;

    /* Gates on the ARGUMENTS, which is the whole point of the callback: "loud" asks, quiet does not. */
    if (call_json != NULL && strstr(call_json, "loud") != NULL)
        return dup_str("{\"display\":\"echo loudly\",\"alwaysAskable\":true}");

    return NULL;
}

const char* cxagent_plugin_start(const char* context_json) {
    (void)context_json;
    return dup_str("{\"ok\":true}");
}

#ifndef FIXTURE_NOINVOKE
const char* cxagent_plugin_invoke(const char* tool_name, const char* call_json) {
    (void)tool_name;
    (void)call_json;

#ifdef FIXTURE_CRASH
    /* A REAL segfault — not a thrown exception, which cxagent_plugin.h forbids crossing this
     * boundary anyway. This is the failure mode the out-of-process host exists to survive. */
    int* p = NULL;
    return (const char*)(intptr_t)(*p);
#elif defined(FIXTURE_MALFORMED)
    return dup_str("{ this is not json");
#else
    return dup_str("{\"ok\":true,\"result\":{\"success\":true,\"exitCode\":0,\"errorMessage\":null,"
        "\"permissionDenied\":false,\"decidedBy\":null,\"output\":{\"echoed\":true},"
        "\"logFile\":null,\"durationMs\":1}}");
#endif
}
#endif

const char* cxagent_plugin_stop(void) {
    return dup_str("{\"ok\":true}");
}

#ifdef FIXTURE_COMMANDS
/* ONE DECLARED COMMAND, "greet" — echoes its argument back so the test can tell the argument
 * actually crossed the boundary, not just that some reply came back. Any other name is refused,
 * proving a plugin's own RunCommand (not the host's "no such export" refusal AbiPlugin.RunCommand
 * gives when this export is absent entirely) can answer "refused" on its own terms too. */
const char* cxagent_plugin_command(const char* name, const char* arguments) {
    if (name != NULL && strcmp(name, "greet") == 0) {
        char buf[256];
        snprintf(buf, sizeof(buf), "{\"message\":\"hello, %s\",\"status\":\"reported\"}",
            arguments != NULL ? arguments : "");
        return dup_str(buf);
    }
    return dup_str("{\"message\":\"no such command\",\"status\":\"refused\"}");
}
#endif

#ifdef FIXTURE_SUBMITS
/* ONE SUBMIT, EVER — a static flag rather than a counter because the test reading this only cares
 * about "did exactly one arrive", and returning NULL forever after is what a real plugin does once
 * it has nothing further to say (cxagent_plugin.h: "NULL here means nothing to say right now"). */
static int submitted = 0;

const char* cxagent_plugin_poll(void) {
    if (submitted) return NULL;
    submitted = 1;
    return dup_str("{\"goal\":\"run the tests\",\"wantResult\":false}");
}
#endif

/* A count of every cxagent_plugin_free call, appended as one line per call to the path named by
 * FREE_COUNT_PATH — present only under FIXTURE_MALFORMED, where the test that reads it exercises
 * the parse-failure path specifically. A file rather than a shared counter because the host under
 * test runs as a SEPARATE PROCESS from the assertion reading this value. */
static void record_free(void) {
#ifdef FIXTURE_MALFORMED
    const char* path = getenv("FREE_COUNT_PATH");
    if (path == NULL) return;
    FILE* f = fopen(path, "a");
    if (f == NULL) return;
    fputs("free\n", f);
    fclose(f);
#endif
}

void cxagent_plugin_free(const char* ptr) {
    record_free();
    free((void*)ptr);
}
