using CxAgent.Core.Config;
using CxAgent.Core.Llm;
using CxAgent.Core.Skills;

namespace CxAgent.UI.Config;

/// <summary>
/// An MCP server being edited: the values on screen beside the file's.
///
/// <para>THE COMMAND IS ONE LINE, split as a shell would on save — the way people copy it from a README.</para>
///
/// <para>ENVIRONMENT AND HEADER VALUES ARE WRITE-ONLY, like an API key: that is where tokens live.
/// <see cref="NewEnv"/> and <see cref="NewHeaders"/> start empty; set, they replace the whole map.</para>
/// </summary>
public sealed class McpDraft(string name, McpServerConfig original)
{
    public string Name { get; set; } = name;
    public McpServerConfig Original { get; } = original;
    public bool IsNew { get; init; }

    public string CommandLine { get; set; } = original.IsRemote ? "" : McpRows.Target(original);
    public string? Url { get; set; } = original.Url;
    public string? Cwd { get; set; } = original.WorkingDirectory;
    public int? TimeoutMs { get; set; } = original.TimeoutMs;
    public IReadOnlyDictionary<string, string>? NewEnv { get; set; }
    public IReadOnlyDictionary<string, string>? NewHeaders { get; set; }

    /// <summary>A remote server — a URL rather than a command.</summary>
    public bool IsRemote => !string.IsNullOrWhiteSpace(Url);

    private bool CommandChanged => !IsRemote && CommandLine.Trim() != (Original.IsRemote ? "" : McpRows.Target(Original));
    private bool UrlChanged => Blank(Url) != Blank(Original.Url);

    public bool IsDirty =>
        IsNew || CommandChanged || UrlChanged || Blank(Cwd) != Blank(Original.WorkingDirectory)
        || TimeoutMs != Original.TimeoutMs || NewEnv is not null || NewHeaders is not null;

    /// <summary>The edit: changed keys set, emptied ones cleared, the rest untouched.</summary>
    public McpServerFields ToFields()
    {
        var clear = new HashSet<string>();
        if (Blank(Cwd) is null && Original.WorkingDirectory is not null) clear.Add("cwd");
        if (TimeoutMs is null && Original.TimeoutMs is not null) clear.Add("timeoutMs");
        return new McpServerFields
        {
            Command = CommandChanged || (IsNew && !IsRemote) ? CommandText.SplitArguments(CommandLine.Trim()) : null,
            Url = UrlChanged || (IsNew && IsRemote) ? Blank(Url) : null,
            Cwd = Blank(Cwd) != Blank(Original.WorkingDirectory) ? Blank(Cwd) : null,
            TimeoutMs = TimeoutMs != Original.TimeoutMs ? TimeoutMs : null,
            Env = NewEnv,
            Headers = NewHeaders,
            Clear = clear,
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
