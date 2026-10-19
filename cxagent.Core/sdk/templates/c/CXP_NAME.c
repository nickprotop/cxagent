/*
 * CXP_NAME — counts the lines and words in a file.
 *
 * One tool, written with the cxagent C SDK (cxp/cxp.h). It asks before reading a file outside the
 * working folder, reports progress as it reads and streams a running count, and reads one setting.
 *
 * THE FOLDER CHECK IS A TEACHING ONE. The gate resolves the path and the tool resolves it again, so a
 * link swapped between the two is read unasked; and on Windows _fullpath does not follow junctions,
 * so a junction inside the folder that points out of it counts as inside. A plugin guarding anything
 * that matters opens the file once, in the tool, and checks the handle's final path.
 */
/* realpath is POSIX, not C11: without this a -std=c11 build has no declaration for it. MSVC's /W3 warns
   that fopen is unsafe; it is not, used as here. */
#ifndef _WIN32
#  define _XOPEN_SOURCE 700
#endif
#define _CRT_SECURE_NO_WARNINGS
#include <ctype.h>
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include "cxp.h"

#ifndef PATH_MAX
#  define PATH_MAX 4096
#endif

/* The size travels with the buffer: _fullpath needs it, and sizeof a parameter is the pointer's. */
#ifdef _WIN32
#  define SEPARATOR '\\'
static int full_path(const char *path, char *out, size_t size) { return _fullpath(out, path, size) != NULL; }
static int is_absolute(const char *p) { return p[0] == '/' || p[0] == '\\' || (p[0] && p[1] == ':'); }
/* _stat64: plain stat's st_size is 32-bit on Windows and stops at 2 GB. */
typedef struct _stat64 file_info;
#  define file_stat _stat64
#else
#  define SEPARATOR '/'
static int full_path(const char *path, char *out, size_t size) { (void)size; return realpath(path, out) != NULL; }
static int is_absolute(const char *p) { return p[0] == '/'; }
typedef struct stat file_info;
#  define file_stat stat
#endif

/* The file the call names, resolved against the working folder. NULL when it does not exist. */
static const char *resolve(const cxp_call *call, char *out, size_t size) {
    const char *file = cxp_arg_string(call, "file");
    if (!file) return NULL;
    const char *joined = is_absolute(file) ? file : cxp_format("%s%c%s", cxp_working_directory(), SEPARATOR, file);
    return full_path(joined, out, size) ? out : NULL;
}

static int inside_working_folder(const char *path) {
    char folder[PATH_MAX];
    if (!full_path(cxp_working_directory(), folder, sizeof folder)) return 0;
    size_t n = strlen(folder);
    return strncmp(path, folder, n) == 0 && (path[n] == '\0' || path[n] == SEPARATOR);
}

/* Reading a file outside the working folder is the user's call: ask, naming the file. */
static cxp_gate count_gate(const cxp_call *call) {
    char path[PATH_MAX];
    const char *resolved = resolve(call, path, sizeof path);
    if (!resolved || inside_working_folder(resolved)) return cxp_no_ask();
    return cxp_ask(cxp_format("read %s, outside the working folder", resolved));
}

static cxp_result count(const cxp_call *call) {
    const char *file = cxp_arg_string(call, "file");
    if (!file) return cxp_error("pass a file to count.");

    // A FOLDER IS NOT AN EMPTY FILE: fopen opens one on Linux, and counting it would say "0 lines".
    char path[PATH_MAX];
    file_info info;
    if (!resolve(call, path, sizeof path) || file_stat(path, &info) != 0)
        return cxp_error(cxp_format("cannot read '%s'.", file));
    if ((info.st_mode & S_IFMT) == S_IFDIR) return cxp_error(cxp_format("'%s' is a folder; pass a file.", file));
    FILE *in = fopen(path, "rb");
    if (!in) return cxp_error(cxp_format("cannot read '%s'.", file));

    // THE SIZE FROM stat, in 64 bits: ftell's long is 32-bit on Windows.
    int64_t size = (int64_t)info.st_size;

    int count_blank = cxp_setting_bool("countBlankLines", 0);
    int64_t lines = 0, words = 0, read = 0, next_report = size / 10;
    int in_word = 0, line_has_text = 0, c;
    while ((c = fgetc(in)) != EOF) {
        read++;
        if (c == '\n') {
            // STREAMED WHEN THE COUNT MOVES, so a blank line after the thousandth does not repeat it.
            if (line_has_text || count_blank) {
                lines++;
                if (lines % 1000 == 0) cxp_stream(call, cxp_format("%lld lines… ", (long long)lines));
            }
            line_has_text = 0;
        } else if (!isspace(c)) {
            line_has_text = 1;
        }

        if (isspace(c)) in_word = 0;
        else if (!in_word) { in_word = 1; words++; }

        // PROGRESS EVERY TENTH OF THE FILE: often enough to see it move, rarely enough to cost nothing.
        if (size > 0 && read >= next_report) {
            cxp_progress(call, 100.0 * (double)read / (double)size, cxp_format("%lld lines so far", (long long)lines));
            next_report += size / 10 > 0 ? size / 10 : 1;
        }
    }
    if (line_has_text) lines++;

    // A READ THAT FAILED PART WAY is not a smaller file: the count so far would be a wrong answer.
    int failed = ferror(in);
    fclose(in);
    if (failed) return cxp_error(cxp_format("reading '%s' failed part way through.", file));

    return cxp_ok_text(cxp_format("%s: %lld %s, %lld %s", file,
        (long long)lines, lines == 1 ? "line" : "lines", (long long)words, words == 1 ? "word" : "words"));
}

CXP_PLUGIN(
    CXP_TOOL("CXP_NAME_count", count, count_gate)
)
