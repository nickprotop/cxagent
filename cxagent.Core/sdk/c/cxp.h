/*
 * cxp — the C plugin SDK for cxagent.
 *
 * Write tools, not ABI glue: register them with CXP_PLUGIN(CXP_TOOL(...)), describe them in the
 * sidecar, and build with cxp_add_plugin() from cxp.cmake. cxp.c implements every export
 * cxagent_plugin.h declares over the table CXP_PLUGIN defines.
 *
 * MEMORY: nothing cxp hands you needs freeing. Strings from cxp_format live until the call that made
 * them returns; results, gates and command results are owned by cxp once returned.
 *
 * THREADS: calls may run at the same time, each on its own thread. A cxp_call is valid only during
 * its call; to report from a thread of your own, copy cxp_call_id() and use the _any forms.
 */
#ifndef CXP_H
#define CXP_H

#include <stdint.h>
#include "cxagent_plugin.h"
#include "cJSON.h"

#ifdef __cplusplus
extern "C" {
#endif

/* A call in progress: its tool, its arguments, and the id its reports name. */
typedef struct cxp_call cxp_call;

/* What a tool returns. Build one with cxp_ok_text, cxp_ok_json or cxp_error — never by hand. */
typedef struct { char *envelope; } cxp_result;

/* What a gate returns: cxp_no_ask(), cxp_ask(display) or cxp_ask_every_time(display) — never by hand,
   since cxp frees display. */
typedef struct { char *display; int ask; int always_askable; } cxp_gate;

/* What a command returns: cxp_reported(message), cxp_changed(message) or cxp_refused(message) — never
   by hand, since cxp frees message. */
typedef struct { char *message; const char *status; } cxp_command_result;

typedef cxp_result (*cxp_tool_fn)(const cxp_call *call);
typedef cxp_gate (*cxp_gate_fn)(const cxp_call *call);
typedef cxp_command_result (*cxp_command_fn)(const char *arguments);

/* One row of the table CXP_PLUGIN defines. */
typedef struct {
    int kind;                 /* 1 a tool, 2 a command, 0 the end of the table */
    const char *name;
    cxp_tool_fn tool;
    cxp_gate_fn gate;         /* NULL for a tool the sidecar gates true or false; a "dynamic" tool with
                                 no gate function asks on every call */
    cxp_command_fn command;
} cxp_entry;

#define CXP_TOOL(name, fn, gate) { 1, (name), (fn), (gate), 0 }
#define CXP_COMMAND(name, fn) { 2, (name), 0, 0, (fn) }
/* EXTERNAL LINKAGE IN C++ TOO: there a namespace-scope const would be internal, and cxp.c — compiled
   as C — could not find the table. */
#ifdef __cplusplus
#  define CXP_PLUGIN(...) extern "C" const cxp_entry cxp_entries[] = { __VA_ARGS__, { 0, 0, 0, 0, 0 } };
#else
#  define CXP_PLUGIN(...) const cxp_entry cxp_entries[] = { __VA_ARGS__, { 0, 0, 0, 0, 0 } };
#endif

/* Arguments: NULL, or the fallback, when the key is missing or holds another type. */
const char *cxp_arg_string(const cxp_call *call, const char *name);
double cxp_arg_number(const cxp_call *call, const char *name, double fallback);
int cxp_arg_bool(const cxp_call *call, const char *name, int fallback);
const cJSON *cxp_arg_json(const cxp_call *call, const char *name);

/* The start context: the folder the plugin works in, and its settings block. */
const char *cxp_working_directory(void);
const char *cxp_setting_string(const char *name);
double cxp_setting_number(const char *name, double fallback);
int cxp_setting_bool(const char *name, int fallback);

/* Results. cxp_ok_json takes ownership of output — a cJSON object whose "content" the model reads. */
cxp_result cxp_ok_text(const char *text);
cxp_result cxp_ok_json(cJSON *output);
cxp_result cxp_error(const char *message);

/* Gates. cxp_ask offers "Always"; cxp_ask_every_time never does. */
cxp_gate cxp_no_ask(void);
cxp_gate cxp_ask(const char *display);
cxp_gate cxp_ask_every_time(const char *display);

/* Commands. */
cxp_command_result cxp_reported(const char *message);
cxp_command_result cxp_changed(const char *message);
cxp_command_result cxp_refused(const char *message);

/* The client: queue a turn to start, as the user typing goal would. Needs "client": true in the
   sidecar. Returns 0 when queued. */
int cxp_submit(const char *goal);

/* Reporting while a call runs. Progress and stream need a call. cxp_log with a NULL call goes to the
   plugin's own log when made outside any call — from start, say; inside a tool it goes to that call. */
void cxp_progress(const cxp_call *call, double percent, const char *message);
void cxp_log(const cxp_call *call, int level, const char *line);
void cxp_stream(const cxp_call *call, const char *text);

/* From a thread of your own: copy cxp_call_id() during the call, report with the copy later.
   A report for a call that has already returned is dropped by cxagent. */
const char *cxp_call_id(const cxp_call *call);
void cxp_progress_any(const char *call_id, double percent, const char *message);
void cxp_log_any(const char *call_id, int level, const char *line);
void cxp_stream_any(const char *call_id, const char *text);

/* printf into a string that lives until the current call returns. Free nothing yourself. On a thread
   of your own it is never released: use your own buffers there. */
char *cxp_format(const char *format, ...)
#if defined(__GNUC__) || defined(__clang__)
    __attribute__((format(printf, 1, 2)))
#endif
    ;

#ifdef __cplusplus
}
#endif

#endif
