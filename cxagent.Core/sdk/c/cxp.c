/*
 * cxp.c — every export cxagent_plugin.h declares, over the table an author's CXP_PLUGIN(...) defines.
 *
 * Built by cxp_add_plugin() (cxp.cmake), which supplies:
 *   CXP_CONTRACT      the sidecar's "pluginContract", returned by the handshake
 *   cxp_manifest.h    the sidecar as CXP_MANIFEST, returned by describe
 *   CXP_CLIENT        defined when the sidecar says "client": true — poll is exported then only
 *   CXP_COMMANDS      defined when the sidecar declares commands — command is exported then only
 */
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "cxp.h"
#include "cxp_manifest.h"

#ifndef CXP_CONTRACT
#  error "build with cxp_add_plugin() from cxp.cmake: it defines CXP_CONTRACT from the sidecar"
#endif

#ifdef _WIN32
#  include <windows.h>
typedef SRWLOCK cxp_lock;
#  define CXP_LOCK_INIT SRWLOCK_INIT
#  define cxp_lock_take(l) AcquireSRWLockExclusive(l)
#  define cxp_lock_give(l) ReleaseSRWLockExclusive(l)
#  define CXP_THREAD_LOCAL __declspec(thread)
#else
#  include <pthread.h>
typedef pthread_mutex_t cxp_lock;
#  define CXP_LOCK_INIT PTHREAD_MUTEX_INITIALIZER
#  define cxp_lock_take(l) pthread_mutex_lock(l)
#  define cxp_lock_give(l) pthread_mutex_unlock(l)
#  define CXP_THREAD_LOCAL _Thread_local
#endif

extern const cxp_entry cxp_entries[];

struct cxp_call {
    const char *tool;
    cJSON *arguments;   /* the call's "arguments" object; owned by the call's parsed JSON */
    char *id;           /* this call's id, copied from current_call(); NULL when not attached */
};

static const cxagent_host *g_host;
static cJSON *g_settings;           /* the start context's settings object, or NULL */
static char *g_working_directory;

/* ---- allocation: every string handed to cxagent is cxp's, released by cxagent_plugin_free ---- */

/* RETURNED WHEN EVEN THE ERROR CANNOT BE ALLOCATED: static, so free must never release it. */
static const char OUT_OF_MEMORY[] = "{\"ok\":false,\"error\":\"cxp: out of memory\"}";

#ifdef CXP_COUNT_ALLOCATIONS
static cxp_lock g_count_lock = CXP_LOCK_INIT;
static long g_outstanding;
static void count(long delta) { cxp_lock_take(&g_count_lock); g_outstanding += delta; cxp_lock_give(&g_count_lock); }
long cxp_test_outstanding(void) { cxp_lock_take(&g_count_lock); long n = g_outstanding; cxp_lock_give(&g_count_lock); return n; }
#else
static void count(long delta) { (void)delta; }
#endif

/* A heap string for cxagent: counted out here, counted back in cxagent_plugin_free. */
static const char *hand_over(char *text) {
    if (!text) return OUT_OF_MEMORY;
    count(1);
    return text;
}

static char *copy(const char *text) {
    if (!text) return NULL;
    size_t n = strlen(text) + 1;
    char *out = malloc(n);
    if (out) memcpy(out, text, n);
    return out;
}

/* ---- the per-call arena: cxp_format strings, freed when the export that made them returns ---- */

typedef struct arena_item { struct arena_item *next; } arena_item;
static CXP_THREAD_LOCAL arena_item *t_arena;

char *cxp_format(const char *format, ...) {
    va_list args;
    va_start(args, format);
    va_list measure;
    va_copy(measure, args);
    int n = vsnprintf(NULL, 0, format, measure);
    va_end(measure);
    if (n < 0) { va_end(args); return NULL; }

    arena_item *item = malloc(sizeof(arena_item) + (size_t)n + 1);
    if (!item) { va_end(args); return NULL; }
    char *text = (char *)(item + 1);
    vsnprintf(text, (size_t)n + 1, format, args);
    va_end(args);

    item->next = t_arena;
    t_arena = item;
    return text;
}

static void arena_release(void) {
    while (t_arena) {
        arena_item *next = t_arena->next;
        free(t_arena);
        t_arena = next;
    }
}

/* ---- results ---- */

static char *print(cJSON *value) {
    char *text = cJSON_PrintUnformatted(value);
    cJSON_Delete(value);
    return text;
}

/* A JobResult inside an envelope: the call crossed the boundary; result.success says how the tool did. */
static cxp_result result(int success, const char *message, cJSON *output) {
    cJSON *root = cJSON_CreateObject();
    cJSON_AddTrueToObject(root, "ok");
    cJSON *res = cJSON_AddObjectToObject(root, "result");
    cJSON_AddBoolToObject(res, "success", success);
    cJSON_AddNumberToObject(res, "exitCode", success ? 0 : 1);
    if (message) cJSON_AddStringToObject(res, "errorMessage", message);
    else cJSON_AddNullToObject(res, "errorMessage");
    cJSON_AddItemToObject(res, "output", output ? output : cJSON_CreateObject());
    cxp_result r = { print(root) };
    return r;
}

cxp_result cxp_ok_text(const char *text) {
    cJSON *output = cJSON_CreateObject();
    cJSON_AddStringToObject(output, "content", text ? text : "");
    return result(1, NULL, output);
}

cxp_result cxp_ok_json(cJSON *output) {
    return result(1, NULL, cJSON_IsObject(output) ? output : (cJSON_Delete(output), cJSON_CreateObject()));
}

cxp_result cxp_error(const char *message) {
    return result(0, message ? message : "the tool failed", NULL);
}

/* ---- gates and commands ---- */

cxp_gate cxp_no_ask(void) { cxp_gate g = { NULL, 0, 0 }; return g; }
cxp_gate cxp_ask(const char *display) { cxp_gate g = { copy(display), 1, 1 }; return g; }
cxp_gate cxp_ask_every_time(const char *display) { cxp_gate g = { copy(display), 1, 0 }; return g; }

cxp_command_result cxp_reported(const char *message) { cxp_command_result r = { copy(message), "reported" }; return r; }
cxp_command_result cxp_changed(const char *message) { cxp_command_result r = { copy(message), "changed" }; return r; }
cxp_command_result cxp_refused(const char *message) { cxp_command_result r = { copy(message), "refused" }; return r; }

/* ---- arguments and the start context ---- */

static const cJSON *member(const cJSON *object, const char *name) {
    return object && name ? cJSON_GetObjectItemCaseSensitive(object, name) : NULL;
}

const char *cxp_arg_string(const cxp_call *call, const char *name) {
    const cJSON *v = member(call ? call->arguments : NULL, name);
    return cJSON_IsString(v) ? v->valuestring : NULL;
}

double cxp_arg_number(const cxp_call *call, const char *name, double fallback) {
    const cJSON *v = member(call ? call->arguments : NULL, name);
    return cJSON_IsNumber(v) ? v->valuedouble : fallback;
}

int cxp_arg_bool(const cxp_call *call, const char *name, int fallback) {
    const cJSON *v = member(call ? call->arguments : NULL, name);
    return cJSON_IsBool(v) ? cJSON_IsTrue(v) : fallback;
}

const cJSON *cxp_arg_json(const cxp_call *call, const char *name) {
    return member(call ? call->arguments : NULL, name);
}

const char *cxp_working_directory(void) { return g_working_directory ? g_working_directory : "."; }

const char *cxp_setting_string(const char *name) {
    const cJSON *v = member(g_settings, name);
    return cJSON_IsString(v) ? v->valuestring : NULL;
}

double cxp_setting_number(const char *name, double fallback) {
    const cJSON *v = member(g_settings, name);
    return cJSON_IsNumber(v) ? v->valuedouble : fallback;
}

int cxp_setting_bool(const char *name, int fallback) {
    const cJSON *v = member(g_settings, name);
    return cJSON_IsBool(v) ? cJSON_IsTrue(v) : fallback;
}

/* ---- reporting ---- */

const char *cxp_call_id(const cxp_call *call) { return call ? call->id : NULL; }

/* NULL TO THE HOST means "the call running on this thread" — right for a report made inside the
   call, which is where a cxp_call is valid. */
void cxp_progress(const cxp_call *call, double percent, const char *message) {
    if (g_host && call) g_host->progress(call->id, percent, message);
}

void cxp_log(const cxp_call *call, int level, const char *line) {
    if (g_host) g_host->log(call ? call->id : NULL, (int32_t)level, line);
}

void cxp_stream(const cxp_call *call, const char *text) {
    if (g_host && call) g_host->stream(call->id, text);
}

void cxp_progress_any(const char *call_id, double percent, const char *message) {
    if (g_host && call_id && *call_id) g_host->progress(call_id, percent, message);
}

void cxp_log_any(const char *call_id, int level, const char *line) {
    if (g_host) g_host->log(call_id && *call_id ? call_id : NULL, (int32_t)level, line);
}

void cxp_stream_any(const char *call_id, const char *text) {
    if (g_host && call_id && *call_id) g_host->stream(call_id, text);
}

/* ---- the client: goals queued here, handed out one per poll ---- */

#ifdef CXP_CLIENT
typedef struct goal { struct goal *next; char *text; } goal;
static cxp_lock g_goal_lock = CXP_LOCK_INIT;
static goal *g_goals, *g_goals_tail;
#endif

int cxp_submit(const char *text) {
#ifdef CXP_CLIENT
    if (!text || !*text) return -1;
    goal *g = malloc(sizeof(goal));
    if (!g || !(g->text = copy(text))) { free(g); return -1; }
    g->next = NULL;
    cxp_lock_take(&g_goal_lock);
    if (g_goals_tail) g_goals_tail->next = g; else g_goals = g;
    g_goals_tail = g;
    cxp_lock_give(&g_goal_lock);
    return 0;
#else
    /* NO "client": true IN THE SIDECAR, so no poll is exported and nothing would ever take this. */
    (void)text;
    return -1;
#endif
}

/* ---- finding an entry ---- */

static const cxp_entry *find(int kind, const char *name) {
    for (const cxp_entry *e = cxp_entries; e->kind; e++)
        if (e->kind == kind && name && strcmp(e->name, name) == 0) return e;
    return NULL;
}

/* A call for a tool, from call_json — the tool's arguments object, for invoke and gate alike. The
   parsed JSON stays alive in *root until the call ends. */
static cxp_call make_call(const char *tool, const char *call_json, cJSON **root) {
    cxp_call call = { tool, NULL, NULL };
    *root = cJSON_Parse(call_json ? call_json : "{}");
    call.arguments = cJSON_IsObject(*root) ? *root : NULL;
    if (g_host && g_host->current_call) call.id = copy(g_host->current_call());
    return call;
}

static void end_call(cxp_call *call, cJSON *root) {
    free(call->id);
    cJSON_Delete(root);
    arena_release();
}

/* ---- the exports ---- */

CXAGENT_PLUGIN_EXPORT int32_t cxagent_plugin_abi_version(void) { return CXP_CONTRACT; }

CXAGENT_PLUGIN_EXPORT const char *cxagent_plugin_describe(void) { return hand_over(copy(CXP_MANIFEST)); }

CXAGENT_PLUGIN_EXPORT void cxagent_plugin_attach(const cxagent_host *host) { g_host = host; }

CXAGENT_PLUGIN_EXPORT const char *cxagent_plugin_start(const char *context_json) {
    cJSON *context = cJSON_Parse(context_json ? context_json : "{}");
    const cJSON *wd = member(context, "workingDirectory");
    free(g_working_directory);
    g_working_directory = cJSON_IsString(wd) ? copy(wd->valuestring) : NULL;
    cJSON_Delete(g_settings);
    g_settings = cJSON_DetachItemFromObjectCaseSensitive(context, "settings");
    cJSON_Delete(context);
    return hand_over(copy("{\"ok\":true}"));
}

CXAGENT_PLUGIN_EXPORT const char *cxagent_plugin_invoke(const char *tool_name, const char *call_json) {
    const cxp_entry *entry = find(1, tool_name);
    if (!entry || !entry->tool) {
        char *text = cxp_error(cxp_format("no tool '%s' in this plugin", tool_name ? tool_name : "")).envelope;
        arena_release();
        return hand_over(text);
    }

    cJSON *root;
    cxp_call call = make_call(tool_name, call_json, &root);
    cxp_result r = entry->tool(&call);

    // A TOOL THAT RETURNED NO RESULT is a failed call naming it — never NULL to the host, which
    // must always receive an envelope.
    char *envelope = r.envelope ? r.envelope
        : cxp_error(cxp_format("tool '%s' returned no result", tool_name)).envelope;
    end_call(&call, root);
    return hand_over(envelope);
}

CXAGENT_PLUGIN_EXPORT const char *cxagent_plugin_gate(const char *tool_name, const char *call_json) {
    const cxp_entry *entry = find(1, tool_name);
    if (!entry || !entry->gate) return NULL;

    cJSON *root;
    cxp_call call = make_call(tool_name, call_json, &root);
    cxp_gate g = entry->gate(&call);
    char *text = NULL;
    if (g.ask) {
        cJSON *out = cJSON_CreateObject();
        cJSON_AddStringToObject(out, "display", g.display ? g.display : "");
        cJSON_AddBoolToObject(out, "alwaysAskable", g.always_askable);
        text = print(out);
    }

    free(g.display);
    end_call(&call, root);
    return text ? hand_over(text) : NULL;
}

CXAGENT_PLUGIN_EXPORT const char *cxagent_plugin_stop(void) { return hand_over(copy("{\"ok\":true}")); }

#ifdef CXP_CLIENT
CXAGENT_PLUGIN_EXPORT const char *cxagent_plugin_poll(void) {
    cxp_lock_take(&g_goal_lock);
    goal *g = g_goals;
    if (g) {
        g_goals = g->next;
        if (!g_goals) g_goals_tail = NULL;
    }
    cxp_lock_give(&g_goal_lock);
    if (!g) return NULL;

    cJSON *out = cJSON_CreateObject();
    cJSON_AddStringToObject(out, "goal", g->text);
    cJSON_AddFalseToObject(out, "wantResult");
    free(g->text);
    free(g);
    return hand_over(print(out));
}
#endif

#ifdef CXP_COMMANDS
CXAGENT_PLUGIN_EXPORT const char *cxagent_plugin_command(const char *name, const char *arguments) {
    const cxp_entry *entry = find(2, name);
    cJSON *out = cJSON_CreateObject();
    if (!entry || !entry->command) {
        cJSON_AddStringToObject(out, "message", "no such command in this plugin");
        cJSON_AddStringToObject(out, "status", "refused");
        return hand_over(print(out));
    }

    cxp_command_result r = entry->command(arguments ? arguments : "");
    if (r.message) cJSON_AddStringToObject(out, "message", r.message);
    else cJSON_AddNullToObject(out, "message");
    cJSON_AddStringToObject(out, "status", r.status ? r.status : "reported");
    free(r.message);
    arena_release();
    return hand_over(print(out));
}
#endif

CXAGENT_PLUGIN_EXPORT void cxagent_plugin_free(const char *ptr) {
    if (!ptr || ptr == OUT_OF_MEMORY) return;
    count(-1);
    free((void *)ptr);
}
