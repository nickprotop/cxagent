/* A plugin written with the C SDK, exercising every helper — what CxpSdkTests drives through the real
 * plugin host. Built by AbiFixtures/CMakeLists.txt with cxp_add_plugin and CXP_COUNT_ALLOCATIONS. */
/* usleep is POSIX, not C11: ask for it before any header, or strict -std=c11 hides it. */
#define _DEFAULT_SOURCE
#include <stdio.h>
#include <string.h>
#include "cxp.h"

#ifdef _WIN32
#  include <windows.h>
#else
#  include <pthread.h>
#  include <unistd.h>
#endif

static cxp_result echo(const cxp_call *call) {
    const char *text = cxp_arg_string(call, "text");
    return text ? cxp_ok_text(cxp_format("echo: %s", text)) : cxp_error("text is required");
}

static cxp_result typed(const cxp_call *call) {
    return cxp_ok_text(cxp_format("%g %d %s", cxp_arg_number(call, "n", -1), cxp_arg_bool(call, "b", -1),
                                  cxp_arg_json(call, "o") ? "json" : "none"));
}

static cxp_result setting(const cxp_call *call) {
    (void)call;
    const char *greeting = cxp_setting_string("greeting");
    return cxp_ok_text(greeting ? greeting : "none");
}

static cxp_result reports(const cxp_call *call) {
    cxp_progress(call, 50, "half");
    cxp_log(call, 2, "working");
    cxp_stream(call, "one ");
    cxp_stream(call, "two");
    return cxp_ok_text("reported");
}

static cxp_result nothing(const cxp_call *call) {
    (void)call;
    cxp_result empty = { 0 };
    return empty;
}

static cxp_result submits(const cxp_call *call) {
    (void)call;
    return cxp_submit("tidy up") == 0 ? cxp_ok_text("queued") : cxp_error("no client");
}

/* A REPORT FROM THE PLUGIN'S OWN THREAD, AFTER ITS CALL RETURNED: cxagent must drop it. */
static char g_late_id[64];
#ifdef _WIN32
static DWORD WINAPI report_late(LPVOID unused) { (void)unused; Sleep(200); cxp_progress_any(g_late_id, 99, "late"); return 0; }
#else
static void *report_late(void *unused) { (void)unused; usleep(200 * 1000); cxp_progress_any(g_late_id, 99, "late"); return NULL; }
#endif

static cxp_result late(const cxp_call *call) {
    const char *id = cxp_call_id(call);
    snprintf(g_late_id, sizeof g_late_id, "%s", id ? id : "");
#ifdef _WIN32
    CloseHandle(CreateThread(NULL, 0, report_late, NULL, 0, NULL));
#else
    pthread_t thread;
    pthread_create(&thread, NULL, report_late, NULL);
    pthread_detach(thread);
#endif
    return cxp_ok_text("started");
}

#ifdef CXP_COUNT_ALLOCATIONS
long cxp_test_outstanding(void);
static cxp_result outstanding(const cxp_call *call) {
    (void)call;
    return cxp_ok_text(cxp_format("%ld", cxp_test_outstanding()));
}
#endif

static cxp_gate ask_once(const cxp_call *call) { return cxp_ask(cxp_format("echo %s", cxp_arg_string(call, "text"))); }
static cxp_gate ask_always(const cxp_call *call) { (void)call; return cxp_ask_every_time("every time"); }
static cxp_command_result ping(const char *arguments) { return cxp_reported(cxp_format("pong %s", arguments)); }

CXP_PLUGIN(
    CXP_TOOL("echo", echo, ask_once),
    CXP_TOOL("typed", typed, 0),
    CXP_TOOL("setting", setting, 0),
    CXP_TOOL("reports", reports, 0),
    CXP_TOOL("nothing", nothing, 0),
    CXP_TOOL("submits", submits, 0),
    CXP_TOOL("always", echo, ask_always),
    CXP_TOOL("late", late, 0),
    CXP_TOOL("outstanding", outstanding, 0),
    CXP_COMMAND("cxp-ping", ping)
)
