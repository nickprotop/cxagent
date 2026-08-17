using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Agent;

/// <summary>
/// What a process needs before it can run sessions.
///
/// <para>A RECORD BECAUSE THE LIST GREW BY ACCRETION. Create took a paths argument, then a gate hook,
/// then an MCP toolset, then a configuration — four parameters, three of them optional and two of
/// them nullable delegates, which is the shape where a caller passes the right things in the wrong
/// order and the compiler agrees. Naming the group makes that a build error: they were one concept
/// (this process's setup) wearing four signatures.</para>
///
/// <para>ONLY <see cref="Paths"/> IS REQUIRED, and everything else has a defensible default: no gate
/// means nothing is ever asked for permission, which is an ordinary headless arrangement; no toolset
/// means no MCP servers; no config means read the one in <see cref="Paths"/>.</para>
/// </summary>
public sealed record ProcessSetup
{
    /// <summary>Where config.json, the databases and the logs live.</summary>
    public required AppPaths Paths { get; init; }

    /// <summary>
    /// Turns the rules store the manager owns into a gate.
    ///
    /// <para>NOT A GATE INSTANCE, because the gate needs the store and the store is built inside —
    /// and not built internally either, because asking a human needs a window and Core has none.
    /// Null gives an ungated manager: a caller that genuinely has nobody to ask should not have every
    /// operation silently fail.</para>
    /// </summary>
    public Func<PermissionRulesStore, IPermissionGate>? BuildGate { get; init; }

    /// <summary>The MCP servers every session in this process shares, or null for none.</summary>
    public Mcp.McpToolset? Mcp { get; init; }

    /// <summary>
    /// What this process runs unless a session says otherwise.
    ///
    /// <para>NULL READS config.json FROM <see cref="Paths"/>, which is the answer a caller would
    /// assume: the manager holds the config directory, so it can say what that directory contains
    /// without being told twice.</para>
    ///
    /// <para>ENVIRONMENT VARIABLES ARE THE CALLER'S. The default read expands none — a config using
    /// <c>${VAR}</c> needs somebody to say which environment, and a manager reaching for the ambient
    /// one would make a test's result depend on the machine it ran on. A caller that wants expansion
    /// resolves explicitly and passes the result, which is also how --mock and --model arrive.</para>
    /// </summary>
    public ResolvedConfig? Config { get; init; }

    /// <summary>The common case: a config directory and nothing else to say about it.</summary>
    public static ProcessSetup For(AppPaths paths) => new() { Paths = paths };

    /// <summary>The same, naming the directory directly.</summary>
    public static ProcessSetup For(string configDir) => For(new AppPaths(configDir));
}
