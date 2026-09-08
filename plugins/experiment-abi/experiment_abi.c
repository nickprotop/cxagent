/*
 * experiment-abi — the native twin of ../experiment-managed, exercising the SAME two capabilities
 * (IPluginClient.Submit, reached here through cxagent_plugin_poll, and a plugin command) but across
 * the ABI boundary instead of in-process.
 *
 * NOT IN THE CATALOG, DELIBERATELY. plugins.json does not list this plugin — nobody installs an
 * experiment by accident. It exists to be DRIVEN, not used: a drive of the three shipped plugins
 * exercises contract 2 twice (calculator, clone-finder) and once through a language server wrapper
 * (csharp-lsp), and never contract 3, because none of the three has a reason to start work in its
 * own session. This plugin's only job is to give a drive something that does.
 *
 * ONE TOOL, ONE COMMAND, NOTHING ELSE. experiment_submit takes a goal and returns immediately,
 * having recorded it; cxagent_plugin_poll hands that goal to the host on its next tick. The command,
 * experiment-say, echoes its argument straight back — see ../experiment-managed/README.md for why
 * growing this any further would defeat the point of an experiment.
 *
 * WANT_RESULT IS NOT OFFERED. AbiPlugin.cs refuses wantResult:true from a polled submit by design —
 * fire-and-forget is the whole ABI surface on contract 3 (cxagent_plugin.h, "WHY WANTRESULT IS
 * REFUSED"). The managed twin can ask for and wait on an answer because it holds the client
 * directly; this plugin cannot, so its tool schema does not pretend it can.
 *
 * MODELLED ON cxagent.Tests/AbiFixtures/fixture_plugin.c's FIXTURE_SUBMITS build, the only other
 * known-good example of a submit crossing this wire, and on
 * cxagent.Core/examples/CalculatorAbiPlugin/calculator_abi.c for the surrounding export shapes this
 * file otherwise follows.
 *
 * Build:  gcc -shared -fPIC -o experiment_abi.so experiment_abi.c
 */

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- ownership --------------------------------------------------------------------------------
 *
 * EVERY STRING RETURNED FROM HERE IS HEAP-ALLOCATED, because the host frees every one of them
 * through cxagent_plugin_free below. A string literal handed back here would give free() a pointer
 * it does not own.
 */
static const char* heap(const char* text)
{
    size_t n = strlen(text) + 1;
    char* copy = (char*)malloc(n);
    if (copy == NULL) return NULL;
    memcpy(copy, text, n);
    return copy;
}

static const char OOM[] = "{\"ok\":false,\"error\":\"experiment-abi: out of memory.\"}";

/* ---- version -----------------------------------------------------------------------------------
 *
 * The one function that does not allocate: the host reads this before anything else and refuses a
 * version it does not know.
 */
int32_t cxagent_plugin_abi_version(void) { return 3; }

/* ---- describe ------------------------------------------------------------------------------------
 *
 * THE MANIFEST, BYTE FOR BYTE WHAT experiment_abi.plugin.json HOLDS — the host reads that sidecar
 * before calling this and refuses the load if the two disagree (PluginManifestMatch). "client":true
 * is the field the whole plugin exists to exercise: without it PluginResolver never builds an
 * IPluginClient for this instance, and cxagent_plugin_poll's submit would be refused by name
 * (AbiPlugin.OnPluginSubmitted, "polled a submit but never declared the client capability").
 */
static const char MANIFEST[] =
    "{"
      "\"pluginContract\":3,"
      "\"name\":\"experiment-abi\","
      "\"version\":\"0.1.0\","
      "\"spawns\":false,"
      "\"client\":true,"
      "\"instructions\":null,"
      "\"tools\":["
        "{"
          "\"name\":\"experiment_submit\","
          "\"description\":\"Submit a goal into this session, to exercise the plugin client, over the ABI boundary.\","
          "\"inputSchema\":{"
            "\"type\":\"object\","
            "\"properties\":{"
              "\"goal\":{\"type\":\"string\",\"description\":\"What to ask the agent to do, as a user would type it.\"}"
            "},"
            "\"required\":[\"goal\"]"
          "},"
          "\"gated\":false"
        "}"
      "],"
      "\"commands\":["
        "{"
          "\"name\":\"experiment-say\","
          "\"summary\":\"Echo the argument back into the transcript.\","
          "\"arguments\":[{\"name\":\"text\",\"summary\":\"What to echo back.\"}]"
        "}"
      "]"
    "}";

const char* cxagent_plugin_describe(void)
{
    const char* p = heap(MANIFEST);
    return p != NULL ? p : OOM;
}

/* ---- gate ----------------------------------------------------------------------------------------
 *
 * EVERY PLUGIN FROM CONTRACT 2 ON EXPORTS THIS, including one that gates nothing — see
 * cxagent_plugin.h, "one export table for every plugin rather than two shapes a host must tell
 * apart." experiment_submit is ungated, so this always returns NULL.
 */
const char* cxagent_plugin_gate(const char* tool_name, const char* call_json)
{
    (void)tool_name;
    (void)call_json;
    return NULL;
}

/* ---- start / stop --------------------------------------------------------------------------------
 *
 * NOTHING TO DO, AND THE FUNCTIONS EXIST ANYWAY — this plugin holds no connection and spawns no
 * process, so both are one line, same as experiment-managed's Start/Stop.
 */
const char* cxagent_plugin_start(const char* context_json)
{
    (void)context_json;
    const char* p = heap("{\"ok\":true}");
    return p != NULL ? p : OOM;
}

const char* cxagent_plugin_stop(void)
{
    const char* p = heap("{\"ok\":true}");
    return p != NULL ? p : OOM;
}

/* ---- invoke / poll: the one submit, held between the two -----------------------------------------
 *
 * INVOKE CANNOT SUBMIT DIRECTLY — there is no IPluginClient on this side of the boundary, only
 * cxagent_plugin_poll, which the HOST calls on its own schedule (cxagent_plugin.h, "the host calls
 * this periodically instead"). So experiment_submit's whole job is to remember the goal it was asked
 * for; poll picks it up on its next tick and hands it to the host, which is where the actual
 * IPluginClient.Submit call happens (AbiPlugin.OnPluginSubmitted).
 *
 * ONE PENDING GOAL AT A TIME, DELIBERATELY. A real plugin might queue several; this one exists to
 * prove the wire works, not to prove a queue does, so a second experiment_submit before the first is
 * polled simply overwrites the pending goal — reported back to the caller rather than silently
 * dropped, so a drive that calls it twice quickly sees why only one arrived.
 *
 * "goal" IS READ BY HAND, NO JSON PARSER, matching calculator_abi.c's own choice: this file exists to
 * show the exact bytes crossing the boundary, not to demonstrate a JSON library.
 */
static char pending_goal[4096];
static int has_pending = 0;

static int copy_string_after(const char* json, const char* key, char* out, size_t out_size)
{
    const char* at = strstr(json, key);
    if (at == NULL) return 0;
    at = strchr(at, ':');
    if (at == NULL) return 0;
    at = strchr(at, '"');
    if (at == NULL) return 0;
    at++;
    const char* end = strchr(at, '"');
    if (end == NULL) return 0;

    size_t len = (size_t)(end - at);
    if (len >= out_size) len = out_size - 1;
    memcpy(out, at, len);
    out[len] = '\0';
    return 1;
}

const char* cxagent_plugin_invoke(const char* tool_name, const char* call_json)
{
    if (tool_name == NULL || strcmp(tool_name, "experiment_submit") != 0)
        /* A NAME THIS PLUGIN NEVER DECLARED. It cannot arrive from the host, which dispatches from
           this plugin's own manifest — so reaching here is this plugin's bug. */
        return heap("{\"ok\":false,\"error\":\"experiment-abi: unknown tool.\"}");

    char goal[4096];
    if (call_json == NULL || !copy_string_after(call_json, "\"goal\"", goal, sizeof goal))
        return heap("{\"ok\":true,\"result\":{\"success\":false,"
                    "\"error\":\"experiment_submit needs a 'goal' argument.\"}}");

    /* OVERWRITES ANY GOAL NOT YET POLLED — see the note above this function. */
    int overwrote = has_pending;
    strncpy(pending_goal, goal, sizeof pending_goal - 1);
    pending_goal[sizeof pending_goal - 1] = '\0';
    has_pending = 1;

    char buffer[4224];
    snprintf(buffer, sizeof buffer,
        "{\"ok\":true,\"result\":{\"success\":true,"
        "\"output\":{\"content\":\"queued '%s' for the next poll%s\"}}}",
        goal, overwrote ? " (replacing a goal that had not been polled yet)" : "");

    const char* p = heap(buffer);
    return p != NULL ? p : OOM;
}

/* MAY RETURN NULL, AND ORDINARILY DOES — cxagent_plugin.h: "NULL here means nothing to say right
 * now." Answers with the pending goal exactly once, then clears it; a submit the host refuses (queue
 * full, no agent configured) is reported to this plugin's own logger by the host, not retried here —
 * see AbiPlugin.ReportIfRefused. wantResult is always false: this plugin has no channel to receive an
 * answer on even if it asked for one (cxagent_plugin.h, "WHY WANTRESULT IS REFUSED"). */
const char* cxagent_plugin_poll(void)
{
    if (!has_pending) return NULL;
    has_pending = 0;

    char buffer[4224];
    snprintf(buffer, sizeof buffer, "{\"goal\":\"%s\",\"wantResult\":false}", pending_goal);

    const char* p = heap(buffer);
    return p != NULL ? p : OOM;
}

/* ---- command -------------------------------------------------------------------------------------
 *
 * OPTIONAL EXPORT — cxagent_plugin.h lists this alongside cxagent_plugin_poll as one of the two
 * functions a v3 plugin may omit entirely; NativePlugin.HasCommand resolves it as an optional
 * symbol, so a library built without it still loads. This one exports it to give a drive something
 * to type, matching FIXTURE_COMMANDS in cxagent.Tests/AbiFixtures/fixture_plugin.c, the known-good
 * shape for this export.
 *
 * "arguments" IS THE RAW TEXT A PERSON TYPED, already trimmed by the host before it crosses this
 * boundary (cxagent_plugin.h) — never JSON, unlike every other argument this file receives. So this
 * echoes it back inside a JSON string literal rather than parsing it as one.
 */
const char* cxagent_plugin_command(const char* name, const char* arguments)
{
    if (name == NULL || strcmp(name, "experiment-say") != 0)
        /* A NAME THIS PLUGIN NEVER DECLARED — see cxagent_plugin_invoke's identical unknown-tool
           guard above for why reaching here is this plugin's own bug, not a caller mistake. */
        return heap("{\"message\":\"experiment-abi has no command named that.\",\"status\":\"refused\"}");

    char buffer[4224];
    snprintf(buffer, sizeof buffer,
        "{\"message\":\"experiment-abi says: %s\",\"status\":\"reported\"}",
        arguments != NULL ? arguments : "");

    const char* p = heap(buffer);
    return p != NULL ? p : OOM;
}

/* ---- free ------------------------------------------------------------------------------------- */
void cxagent_plugin_free(const char* ptr)
{
    if (ptr == OOM) return;
    free((void*)ptr);
}
