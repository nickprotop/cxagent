using System.Text.Json;
using CxAgent.Plugins.Testing;

namespace CxAgent.Cli;

/// <summary>
/// <c>cxagent plugin …</c> — the plugin author's commands. Command-line only: no TUI, no model. The
/// work is <see cref="PluginHarness"/>'s, so a test through CxAgent.Plugins.Testing and this command
/// see the same answers; this class parses and prints.
///
/// <para>EXIT CODES: 0 success, 1 the plugin is at fault, 2 usage — so a script tells "my plugin is
/// wrong" from "I typed it wrong" without reading the text.</para>
/// </summary>
public static class PluginCli
{
    private const string Usage = """
        usage: cxagent plugin new <name> --lang csharp [--dir <folder>]
               cxagent plugin check <folder> [--json]
               cxagent plugin call <folder> <tool> ['<json>'] [options]
               cxagent plugin call <folder> --command <name> [arguments…] [--json]
               cxagent plugin pack <folder> [--native <lib>=<rid> …] [--out <folder>] [--description t]
                                   [--publisher t] [--license t] [--repository url] [--json]
               cxagent plugin verify <entry.json | plugins.json> [--json]

        call options:
          --answer allow|deny          how the permission question is answered
          --settings '<json>'          the plugin's settings block (or --settings-file <file>)
          --cwd <folder>               the folder the plugin works in
          --timeout <seconds>          stop the call after this long (default 60)
          --json                       one JSON object instead of live output

        exit: 0 success, 1 the plugin is at fault, 2 usage
        """;

    // THE SAME ESCAPING AS call's OUTPUT — see PluginCallCommand.Indented: a person or a JSON parser
    // reads this, never a page that needs "°" written as "\u00B0".
    private static readonly JsonSerializerOptions Relaxed =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        if (args.Length == 0) return Fail(stderr, "no command.");
        return args[0] switch
        {
            "check" => await Check(args[1..], stdout, stderr, ct).ConfigureAwait(false),
            "call" => await PluginCallCommand.RunAsync(args[1..], stdout, stderr, ct).ConfigureAwait(false),
            "new" => await PluginNewCommand.RunAsync(args[1..], stdout, stderr).ConfigureAwait(false),
            "pack" => await PluginPackCommand.RunAsync(args[1..], stdout, stderr, ct).ConfigureAwait(false),
            "verify" => await PluginVerifyCommand.RunAsync(args[1..], stdout, stderr, ct).ConfigureAwait(false),
            _ => Fail(stderr, $"unknown command '{args[0]}'."),
        };
    }

    private static async Task<int> Check(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var json = args.Contains("--json");
        if (args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal) && a != "--json") is { } unknown)
            return Fail(stderr, $"unknown option '{unknown}'.");
        var folders = args.Where(a => a != "--json").ToArray();
        if (folders.Length != 1) return Fail(stderr, "check takes one plugin folder.");

        // A FOLDER THAT IS NOT THERE IS A TYPO, not a plugin at fault: usage, not 1.
        if (!Directory.Exists(folders[0])) return Fail(stderr, $"no folder '{folders[0]}'.");

        var problems = await PluginHarness.CheckAsync(folders[0], ct: ct).ConfigureAwait(false);
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                ok = problems.Count == 0,
                problems = problems.Select(p => new { field = p.Field, message = p.Message }),
            }, Relaxed));
        }
        else if (problems.Count == 0)
        {
            stdout.WriteLine($"ok — {Path.GetFullPath(folders[0])} loads.");
        }
        else
        {
            foreach (var p in problems) stdout.WriteLine(p.ToString());
        }

        return problems.Count == 0 ? 0 : 1;
    }

    internal static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"cxagent plugin: {message}");
        stderr.WriteLine(Usage);
        return 2;
    }
}
