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
/// <summary>A written file, resolved — ready to go live once the user has said how.</summary>
/// <param name="Write">What the write did.</param>
/// <param name="Fresh">The file resolved, or null when the write failed or it resolved to no provider.</param>
public sealed record PreparedSave(ConfigWrite Write, ResolvedConfig? Fresh);

public static class ConfigApply
{
    private static string PathOf(AppPaths paths) => Path.Combine(paths.ConfigDir, "config.json");

    /// <summary>
    /// Writes the edit and resolves the file. OFF THE UI THREAD: resolving may ask the default
    /// endpoint for its window, which can take seconds.
    /// </summary>
    public static PreparedSave Prepare(ConfigApplyContext ctx, Action<JsonObject> edit)
    {
        var write = ConfigFile.TryApply(new ConfigTarget(PathOf(ctx.Paths), ctx.Env), edit);
        if (!write.Saved) return new PreparedSave(write, null);

        // SAVED BUT NOT LIVE when resolving finds no usable provider: Saved stays true — the file did
        // change — and Errors says why nothing moved; the caller shows both.
        // RESOLVED FROM THE FILE'S DEFAULT: a run started with --model returns to the file's own
        // default for new tabs after a save, the way it would on the next start.
        var fresh = ConfigResolver.Resolve(ctx.Paths, ctx.Env, useMock: false);
        return fresh.HasProvider ? new PreparedSave(write, fresh) : new PreparedSave(write with { Errors = fresh.Errors }, null);
    }

    /// <summary>
    /// The prepared config made current, and the sessions moved by <paramref name="choice"/>. ON THE UI
    /// THREAD, after the user answered the question a cache-breaking change asks.
    /// </summary>
    public static void Commit(ConfigApplyContext ctx, PreparedSave prepared, ConfigChange change, CacheChoice choice)
    {
        if (prepared.Fresh is not { } fresh) return;
        ctx.Current.Replace(fresh);
        ctx.Manager.ApplyConfig(fresh, change, choice);
    }

    /// <summary>
    /// An MCP save: the same reload <c>/mcp reload</c> runs, which restarts the servers and replaces
    /// the one shared toolset in place.
    /// </summary>
    public static async Task<ConfigWrite> SaveMcpAsync(ConfigApplyContext ctx, Action<JsonObject> edit)
    {
        var write = ConfigFile.TryApply(new ConfigTarget(PathOf(ctx.Paths), ctx.Env), edit);
        if (!write.Saved) return write;
        await ctx.Mcp.ReloadAsync(write.Settings!.McpServers, CancellationToken.None);
        return write;
    }
}
