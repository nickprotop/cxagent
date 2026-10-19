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
        usage: cxagent plugin check <folder> [--json]
               cxagent plugin call <folder> <tool> ['<json>'] [options]
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        if (args.Length == 0) return Fail(stderr, "no command.");
        return args[0] switch
        {
            "check" => await Check(args[1..], stdout, stderr, ct).ConfigureAwait(false),
            "call" => Fail(stderr, "call is not available yet."),
            _ => Fail(stderr, $"unknown command '{args[0]}'."),
        };
    }

    private static async Task<int> Check(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var json = args.Contains("--json");
        var folders = args.Where(a => a != "--json").ToArray();
        if (folders.Length != 1) return Fail(stderr, "check takes one plugin folder.");

        var problems = await PluginHarness.CheckAsync(folders[0], ct: ct).ConfigureAwait(false);
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                ok = problems.Count == 0,
                problems = problems.Select(p => new { field = p.Field, message = p.Message }),
            }));
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
