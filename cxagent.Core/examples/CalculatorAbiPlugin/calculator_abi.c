/*
 * A cxagent ABI plugin, in C, in one file.
 *
 * THE SAME CALCULATOR AS ../CalculatorPlugin, DELIBERATELY. Reading the two side by side is the
 * point: the managed one implements IPlugin and is loaded into the host's own process; this one
 * exports six C functions and runs behind a host process that isolates it. Everything else — the
 * tool names, the manifest, the nonsense permission gate — is identical, so the diff between them
 * IS the cost of the boundary and nothing else.
 *
 * WRITE AN ABI PLUGIN WHEN YOUR LANGUAGE CANNOT BE LOADED MANAGED — C, Rust, Go, C++. If you are
 * writing in C#, write a managed plugin: an ABI plugin in .NET needs NativeAOT, which strips the
 * reflection System.Text.Json relies on, and every payload then needs a hand-written JsonTypeInfo.
 * It is more code to reach the same place the host would have loaded directly.
 *
 * NO JSON LIBRARY HERE, AND THAT IS THE EXAMPLE'S POINT. Every string this file returns is a
 * literal or one snprintf. A plugin answering real questions wants a parser (jansson, cJSON); one
 * teaching the boundary wants you to see the exact bytes crossing it.
 *
 * Build:  cc -shared -fPIC -o calculator.so calculator_abi.c
 */

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- ownership -------------------------------------------------------------------------------
 *
 * EVERY STRING RETURNED FROM HERE IS HEAP-ALLOCATED, WITHOUT EXCEPTION, because the host frees
 * every one of them through cxagent_plugin_free below. Returning a string literal would hand
 * free() a pointer it does not own — the crash is not at the return, it is later, inside the host,
 * with a stack that does not mention this file.
 *
 * So: copy, always, even for a constant.
 */
static const char* heap(const char* text)
{
    size_t n = strlen(text) + 1;
    char* copy = (char*)malloc(n);
    if (copy == NULL) return NULL;   /* see the NULL note on describe() */
    memcpy(copy, text, n);
    return copy;
}

/* ---- version ---------------------------------------------------------------------------------
 *
 * The one function that does NOT allocate: the host reads this before anything else and refuses a
 * version it does not know, so a mismatch is a clean message rather than a misparsed struct.
 */
int32_t cxagent_plugin_abi_version(void) { return 1; }

/* ---- describe --------------------------------------------------------------------------------
 *
 * The manifest, byte for byte what calculator.plugin.json holds — the host reads that sidecar
 * before calling this and refuses the load if the two disagree. Two copies of one truth is the
 * price of the sidecar being readable without running the plugin; keeping them in step is yours.
 *
 * NEVER RETURN NULL. The host has no way to tell "out of memory" from "this function is not
 * implemented", so the contract is that a pointer always comes back. Under real memory pressure
 * heap() can still fail — this returns a static sentinel then, and cxagent_plugin_free knows not
 * to free it.
 */
static const char MANIFEST[] =
    "{"
      /* THE HANDSHAKE, REPEATED INSIDE THE MANIFEST. cxagent_plugin_abi_version() answers before
         anything here is read; this field answers again for the manifest itself, so a host holding
         a JSON blob from an unknown source can check it without a live library. Omit it and it
         reads as 0 — an unsupported version, refused at load with a message about a version this
         file never mentioned. */
      "\"abiVersion\":1,"
      "\"name\":\"calculator\","
      "\"version\":\"1.0.0\","
      "\"spawns\":false,"
      "\"instructions\":\"Adds and multiplies two numbers. Addition asks the user for permission "
        "every single time, which is absurd for arithmetic and is exactly why it is here: it makes "
        "the per-call gate visible in an example small enough to read.\","
      "\"tools\":["
        "{"
          "\"name\":\"calc_add\","
          "\"description\":\"Adds two numbers. Asks permission on every call.\","
          "\"inputSchema\":{"
            "\"type\":\"object\","
            "\"properties\":{"
              "\"a\":{\"type\":\"number\",\"description\":\"The first number.\"},"
              "\"b\":{\"type\":\"number\",\"description\":\"The second number.\"}"
            "},"
            "\"required\":[\"a\",\"b\"]"
          "},"
          "\"gated\":true"
        "},"
        "{"
          "\"name\":\"calc_multiply\","
          "\"description\":\"Multiplies two numbers. Never asks.\","
          "\"inputSchema\":{"
            "\"type\":\"object\","
            "\"properties\":{"
              "\"a\":{\"type\":\"number\",\"description\":\"The first number.\"},"
              "\"b\":{\"type\":\"number\",\"description\":\"The second number.\"}"
            "},"
            "\"required\":[\"a\",\"b\"]"
          "},"
          "\"gated\":false"
        "}"
      "]"
    "}";

static const char OOM[] = "{\"ok\":false,\"error\":\"calculator: out of memory.\"}";

const char* cxagent_plugin_describe(void)
{
    const char* p = heap(MANIFEST);
    return p != NULL ? p : OOM;
}

/* ---- start / stop ----------------------------------------------------------------------------
 *
 * NOTHING TO DO, AND THE FUNCTIONS EXIST ANYWAY. A calculator holds no state, opens no connection
 * and spawns no process, so both are one line. They are not optional: the host calls start before
 * the first invoke and stop before it exits, and a missing export fails the load.
 *
 * A plugin that DOES spawn something starts it here — and says "spawns": true in its manifest, so
 * the host records the pid and can reap it if this process dies without reaching stop.
 */
const char* cxagent_plugin_start(const char* context_json)
{
    (void)context_json;   /* working directory and settings; a calculator needs neither */
    const char* p = heap("{\"ok\":true}");
    return p != NULL ? p : OOM;
}

const char* cxagent_plugin_stop(void)
{
    const char* p = heap("{\"ok\":true}");
    return p != NULL ? p : OOM;
}

/* ---- invoke ----------------------------------------------------------------------------------
 *
 * ONE FUNCTION FOR EVERY TOOL, dispatching on tool_name — the ABI has no per-tool entry point, so
 * a plugin offering twelve tools still exports exactly these six functions.
 *
 * READING THE ARGUMENTS BY HAND, because this example refuses to pull in a JSON parser. strstr for
 * the key, atof for the value: enough for two numbers, and wrong the moment an argument is nested
 * or a string contains "a". A real plugin parses properly. This one shows you the bytes.
 */
static double number_after(const char* json, const char* key)
{
    const char* at = strstr(json, key);
    if (at == NULL) return 0.0;
    at = strchr(at, ':');
    return at == NULL ? 0.0 : atof(at + 1);
}

const char* cxagent_plugin_invoke(const char* tool_name, const char* call_json)
{
    double a = number_after(call_json, "\"a\"");
    double b = number_after(call_json, "\"b\"");
    double answer;

    if (strcmp(tool_name, "calc_add") == 0)
        answer = a + b;
    else if (strcmp(tool_name, "calc_multiply") == 0)
        answer = a * b;
    else
        /* A NAME THIS PLUGIN NEVER DECLARED. It cannot arrive from the host, which dispatches from
           this plugin's own manifest — so reaching here is this plugin's bug, and saying so beats
           returning a plausible zero. */
        return heap("{\"ok\":false,\"error\":\"calculator: unknown tool.\"}");

    /* THE RESULT ENVELOPE, NESTED: ok says the CALL crossed the boundary, result.success says the
       WORK succeeded. A tool that fails cleanly is ok:true with success:false — ok:false is for a
       plugin that could not answer at all.

       content IS WHAT THE MODEL READS. A result carrying only structured keys renders to the model
       as an empty string, and it explains the silence rather than reporting it. */
    char buffer[256];
    snprintf(buffer, sizeof buffer,
        "{\"ok\":true,\"result\":{\"success\":true,\"output\":"
        "{\"content\":\"%g\",\"answer\":%g}}}", answer, answer);

    const char* p = heap(buffer);
    return p != NULL ? p : OOM;
}

/* ---- free ------------------------------------------------------------------------------------
 *
 * The host calls this exactly once for every pointer the five functions above returned, in a
 * finally-equivalent — including when it could not parse what it was given.
 *
 * THE SENTINEL CHECK IS NOT DEFENSIVE PROGRAMMING. OOM is static storage; free() on it is
 * undefined behaviour, and the only reason a pointer to it can reach here is that the contract
 * above forbids returning NULL. A plugin with no static fallback does not need this line.
 */
void cxagent_plugin_free(const char* ptr)
{
    if (ptr == NULL || ptr == OOM) return;
    free((void*)ptr);
}
