/*
 * CXP_NAME — counts the lines and words in a file.
 *
 * One tool, written with the cxagent C SDK (cxp/cxp.h). It asks before reading a file outside the
 * working folder, reports progress as it reads and streams a running count, and reads one setting.
 */
#include <ctype.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "cxp.h"

#ifdef _WIN32
#  define FULL_PATH(path, out) _fullpath((out), (path), sizeof (out))
#  define SEPARATOR '\\'
#else
#  define FULL_PATH(path, out) realpath((path), (out))
#  define SEPARATOR '/'
#endif

#ifndef PATH_MAX
#  define PATH_MAX 4096
#endif

/* The file the call names, resolved against the working folder. NULL when it does not exist. */
static const char *resolve(const cxp_call *call, char *out) {
    const char *file = cxp_arg_string(call, "file");
    if (!file) return NULL;
    const char *joined = (file[0] == '/' || file[0] == '\\' || (file[0] && file[1] == ':'))
        ? file
        : cxp_format("%s%c%s", cxp_working_directory(), SEPARATOR, file);
    return FULL_PATH(joined, out) ? out : NULL;
}

static int inside_working_folder(const char *path) {
    char folder[PATH_MAX];
    if (!FULL_PATH(cxp_working_directory(), folder)) return 0;
    size_t n = strlen(folder);
    return strncmp(path, folder, n) == 0 && (path[n] == '\0' || path[n] == SEPARATOR);
}

/* Reading a file outside the working folder is the user's call: ask, naming the file. */
static cxp_gate count_gate(const cxp_call *call) {
    char path[PATH_MAX];
    const char *resolved = resolve(call, path);
    if (!resolved || inside_working_folder(resolved)) return cxp_no_ask();
    return cxp_ask(cxp_format("read %s, outside the working folder", resolved));
}

static cxp_result count(const cxp_call *call) {
    const char *file = cxp_arg_string(call, "file");
    if (!file) return cxp_error("pass a file to count.");

    char path[PATH_MAX];
    FILE *in = resolve(call, path) ? fopen(path, "rb") : NULL;
    if (!in) return cxp_error(cxp_format("cannot read '%s'.", file));

    fseek(in, 0, SEEK_END);
    long size = ftell(in);
    fseek(in, 0, SEEK_SET);

    int count_blank = cxp_setting_bool("countBlankLines", 0);
    long lines = 0, words = 0, read = 0, next_report = size / 10;
    int in_word = 0, line_has_text = 0, c;
    while ((c = fgetc(in)) != EOF) {
        read++;
        if (c == '\n') {
            // STREAMED WHEN THE COUNT MOVES, so a blank line after the thousandth does not repeat it.
            if (line_has_text || count_blank) {
                lines++;
                if (lines % 1000 == 0) cxp_stream(call, cxp_format("%ld lines… ", lines));
            }
            line_has_text = 0;
        } else if (!isspace(c)) {
            line_has_text = 1;
        }

        if (isspace(c)) in_word = 0;
        else if (!in_word) { in_word = 1; words++; }

        // PROGRESS EVERY TENTH OF THE FILE: often enough to see it move, rarely enough to cost nothing.
        if (size > 0 && read >= next_report) {
            cxp_progress(call, 100.0 * (double)read / (double)size, cxp_format("%ld lines so far", lines));
            next_report += size / 10 > 0 ? size / 10 : 1;
        }
    }
    if (line_has_text) lines++;
    fclose(in);

    return cxp_ok_text(cxp_format("%s: %ld lines, %ld words", file, lines, words));
}

CXP_PLUGIN(
    CXP_TOOL("CXP_NAME_count", count, count_gate)
)
