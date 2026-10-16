using System.Text.Json.Nodes;
using CxAgent.Core.Config;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;

namespace CxAgent.UI;

/// <summary>What a save needs to reach the file, the process's config, the sessions and the MCP servers.</summary>
public sealed record ConfigApplyContext(
    AppPaths Paths, IReadOnlyDictionary<string, string> Env, CurrentConfig Current,
    SessionManager Manager, Core.Mcp.McpManager Mcp);

/// <summary>
/// A save, made live: write through the one writer, resolve the file afresh, make that the current
/// config, then move the sessions it touches.
///
/// <para>RESOLVED FROM THE FILE, NOT PATCHED. A fresh resolve rebuilds every provider object, window,
/// default and reviewer from what was just written — the same code startup runs — so nothing the
/// edit changed can be left behind in a cache this class forgot.</para>
/// </summary>
public static class ConfigApply
{
    private static string PathOf(AppPaths paths) => Path.Combine(paths.ConfigDir, "config.json");

    public static ConfigWrite Save(ConfigApplyContext ctx, Action<JsonObject> edit, Func<ConfigWrite, ConfigChange> change)
    {
        var write = ConfigFile.TryApply(PathOf(ctx.Paths), ctx.Env, edit);
        if (!write.Saved) return write;

        var fresh = ConfigResolver.Resolve(ctx.Paths, ctx.Env, useMock: false);
        if (!fresh.HasProvider) return write with { Errors = fresh.Errors };
        ctx.Current.Replace(fresh);
        ctx.Manager.ApplyConfig(fresh, change(write));
        return write;
    }

    /// <summary>
    /// An MCP save: the same reload <c>/mcp reload</c> runs, which restarts the servers and replaces
    /// the one shared toolset in place — no session is re-wired.
    /// </summary>
    public static async Task<ConfigWrite> SaveMcpAsync(ConfigApplyContext ctx, Action<JsonObject> edit)
    {
        var write = ConfigFile.TryApply(PathOf(ctx.Paths), ctx.Env, edit);
        if (!write.Saved) return write;
        await ctx.Mcp.ReloadAsync(write.Settings!.McpServers, CancellationToken.None);
        return write;
    }
}
