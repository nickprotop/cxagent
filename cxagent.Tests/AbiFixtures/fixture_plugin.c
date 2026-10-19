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
 * FIXTURE_DISAGREES: cxagent_plugin_abi_version reports 3 while describe's body says 2 — both in
 * range, so only a check that compares the two catches a plugin contradicting itself.
 * FIXTURE_REPORTS: exports cxagent_plugin_attach and reports through the host's table — start logs
 * with no call; "echo" reports progress, a log line and two streamed chunks from inside the call;
 * "stray" reports for an id never issued and logs with an id that does not parse; "pair" succeeds only
 * while a second "pair" call is in flight. Start also logs a Warning-level line.
 * "echo_dynamic" copies current_call() and reports once more from its own thread, 200ms after the
 * call returned, which the host must drop rather than route anywhere.
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

#ifdef FIXTURE_REPORTS
#  ifdef _WIN32
#    include <windows.h>
#  else
#    include <pthread.h>
#    include <unistd.h>
#  endif
#endif

/* THE ABI'S OWN DECLARATIONS, so every definition below is checked against them and marked for
 * export — without CXAGENT_PLUGIN_EXPORT an MSVC or -fvisibility=hidden build exports nothing. */
#include "cxagent_plugin.h"

/* Every returned string is heap-allocated so cxagent_plugin_free's free() is never asked to
 * release memory it did not itself allocate — cxagent_plugin.h's ownership rule, honoured on the
 * plugin side of the boundary this fixture stands in for. */
static char* dup_str(const char* s) {
    size_t len = strlen(s) + 1;
    char* copy = (char*)malloc(len);
    memcpy(copy, s, len);
    return copy;
}

CXAGENT_PLUGIN_EXPORT int32_t cxagent_plugin_abi_version(void) {
#ifdef FIXTURE_BADVERSION
    return 99;
#elif defined(FIXTURE_DISAGREES) || defined(FIXTURE_SUBMITS)
    /* SUBMITS DECLARES THE CLIENT, which arrived in contract 3 — so it speaks 3, in its handshake
     * and its describe body alike. DISAGREES says 3 here and 2 in its body, on purpose. */
    return 3;
#else
    return 2;
#endif
}

CXAGENT_PLUGIN_EXPORT const char* cxagent_plugin_describe(void) {
    return dup_str(
#ifdef FIXTURE_SUBMITS
        "{\"pluginContract\":3,\"name\":\"fixture\",\"version\":\"1.0.0\",\"instructions\":null,"
#else
        "{\"pluginContract\":2,\"name\":\"fixture\",\"version\":\"1.0.0\",\"instructions\":null,"
#endif
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
CXAGENT_PLUGIN_EXPORT const char* cxagent_plugin_gate(const char* tool_name, const char* call_json) {
    if (tool_name == NULL || strcmp(tool_name, "echo_dynamic") != 0) return NULL;

    /* Gates on the ARGUMENTS, which is the whole point of the callback: "loud" asks, quiet does not. */
    if (call_json != NULL && strstr(call_json, "loud") != NULL)
        return dup_str("{\"display\":\"echo loudly\",\"alwaysAskable\":true}");

    return NULL;
}

#ifdef FIXTURE_REPORTS
/* THE HOST'S TABLE, kept for the library's life as cxagent_plugin.h says it may be. */
static const cxagent_host* g_host = NULL;
static char g_late_call[64];

CXAGENT_PLUGIN_EXPORT void cxagent_plugin_attach(const cxagent_host* host) {
    g_host = host;
}

/* A REPORT FROM THE PLUGIN'S OWN THREAD, AFTER ITS CALL RETURNED: the host must drop it. */
#  ifdef _WIN32
static DWORD WINAPI report_late(LPVOID unused) {
    (void)unused;
    Sleep(200);
    g_host->progress(g_late_call, 99, "late");
    return 0;
}
#  else
static void* report_late(void* unused) {
    (void)unused;
    usleep(200 * 1000);
    g_host->progress(g_late_call, 99, "late");
    return NULL;
}
#  endif

/* A RENDEZVOUS: "pair" succeeds only once a second call is inside "pair" too, and gives up after
 * five seconds. A host that runs calls one at a time can never get two inside at once, so the
 * first call fails — which is how the test tells concurrent invokes from serialized ones without
 * timing anything. A counter and a poll rather than a condition variable, so one body builds with
 * both toolchains. */
static volatile long g_paired = 0;

static int wait_for_pair(void) {
#  ifdef _WIN32
    InterlockedIncrement(&g_paired);
#  else
    __atomic_add_fetch(&g_paired, 1, __ATOMIC_SEQ_CST);
#  endif
    for (int waited = 0; waited < 5000; waited += 10) {
#  ifdef _WIN32
        if (InterlockedCompareExchange(&g_paired, 0, 0) >= 2) return 1;
        Sleep(10);
#  else
        if (__atomic_load_n(&g_paired, __ATOMIC_SEQ_CST) >= 2) return 1;
        usleep(10 * 1000);
#  endif
    }
    return 0;
}
#endif

CXAGENT_PLUGIN_EXPORT const char* cxagent_plugin_start(const char* context_json) {
    (void)context_json;
#ifdef FIXTURE_REPORTS
    /* NOT INSIDE A CALL, so this goes to the plugin's own log. */
    if (g_host != NULL) {
        g_host->log(NULL, 2, "started");
        g_host->log(NULL, 3, "careful");
    }
#endif
    return dup_str("{\"ok\":true}");
}

#ifndef FIXTURE_NOINVOKE
CXAGENT_PLUGIN_EXPORT const char* cxagent_plugin_invoke(const char* tool_name, const char* call_json) {
    (void)tool_name;
    (void)call_json;

#ifdef FIXTURE_CRASH
    /* A REAL segfault — not a thrown exception, which cxagent_plugin.h forbids crossing this
     * boundary anyway. This is the failure mode the out-of-process host exists to survive. */
    int* p = NULL;
    return (const char*)(intptr_t)(*p);
#elif defined(FIXTURE_MALFORMED)
    return dup_str("{ this is not json");
#elif defined(FIXTURE_REPORTS)
    if (tool_name != NULL && strcmp(tool_name, "pair") == 0 && !wait_for_pair())
        return dup_str("{\"ok\":true,\"result\":{\"success\":false,\"exitCode\":1,"
            "\"errorMessage\":\"no second call arrived\",\"permissionDenied\":false,\"decidedBy\":null,"
            "\"output\":{},\"logFile\":null,\"durationMs\":1}}");
    if (g_host != NULL && tool_name != NULL && strcmp(tool_name, "stray") == 0) {
        /* AN ID THE HOST NEVER ISSUED, AND ONE THAT IS NOT AN ID AT ALL: both reach no call. */
        g_host->progress("999999", 10, "stray");
        g_host->log("not-a-number", 2, "unplaceable");
    }
    if (g_host != NULL && tool_name != NULL && strcmp(tool_name, "echo") == 0) {
        /* NULL call ids: this thread's call, so a report from inside invoke needs no id. */
        g_host->progress(NULL, 50, "half");
        g_host->log(NULL, 2, "working");
        g_host->stream(NULL, "one ");
        g_host->stream(NULL, "two");
    } else if (g_host != NULL && tool_name != NULL && strcmp(tool_name, "echo_dynamic") == 0) {
        const char* id = g_host->current_call();
        strncpy(g_late_call, id != NULL ? id : "", sizeof g_late_call - 1);
#  ifdef _WIN32
        CloseHandle(CreateThread(NULL, 0, report_late, NULL, 0, NULL));
#  else
        pthread_t t;
        pthread_create(&t, NULL, report_late, NULL);
        pthread_detach(t);
#  endif
    }
    return dup_str("{\"ok\":true,\"result\":{\"success\":true,\"exitCode\":0,\"errorMessage\":null,"
        "\"permissionDenied\":false,\"decidedBy\":null,\"output\":{\"echoed\":true},"
        "\"logFile\":null,\"durationMs\":1}}");
#else
    return dup_str("{\"ok\":true,\"result\":{\"success\":true,\"exitCode\":0,\"errorMessage\":null,"
        "\"permissionDenied\":false,\"decidedBy\":null,\"output\":{\"echoed\":true},"
        "\"logFile\":null,\"durationMs\":1}}");
#endif
}
#endif

CXAGENT_PLUGIN_EXPORT const char* cxagent_plugin_stop(void) {
    return dup_str("{\"ok\":true}");
}

#ifdef FIXTURE_COMMANDS
/* ONE DECLARED COMMAND, "greet" — echoes its argument back so the test can tell the argument
 * actually crossed the boundary, not just that some reply came back. Any other name is refused,
 * proving a plugin's own RunCommand (not the host's "no such export" refusal AbiPlugin.RunCommand
 * gives when this export is absent entirely) can answer "refused" on its own terms too. */
CXAGENT_PLUGIN_EXPORT const char* cxagent_plugin_command(const char* name, const char* arguments) {
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

CXAGENT_PLUGIN_EXPORT const char* cxagent_plugin_poll(void) {
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

CXAGENT_PLUGIN_EXPORT void cxagent_plugin_free(const char* ptr) {
    record_free();
    free((void*)ptr);
}
