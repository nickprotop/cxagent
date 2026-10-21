using TS = TreeSitter;

namespace CxAgent.Plugins.RepoMap.Parsing;

/// <summary>
/// Whether Tree-sitter's native libraries load in this process.
///
/// <para>ASKED ONCE PER PROCESS. A library that failed to load does not start loading on the next
/// call, and every map would otherwise pay for the same failure again. The answer is the reason a
/// map reads everything as text, so it is kept as words, not as a flag.</para>
/// </summary>
public static class Natives
{
    private static readonly Lazy<string?> Probed = new(Probe);

    /// <summary>Null when the natives load; otherwise why not, for the map's header.</summary>
    public static string? Problem => Probed.Value;

    /// <summary>
    /// Loads the core library and one grammar and parses a line, or says why that failed.
    ///
    /// <para>A REAL PARSE, NOT A FILE CHECK. A library that is present but built for another libc, or
    /// a grammar whose ABI the core does not accept, fails only when it is used.</para>
    /// </summary>
    public static string? Probe()
    {
        try
        {
            using var language = new TS.Language("python");
            using var parser = new TS.Parser(language);
            using var tree = parser.Parse("x = 1\n");
            return tree is null ? "the parser returned no tree" : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or BadImageFormatException or PlatformNotSupportedException
                                       or TypeInitializationException or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
