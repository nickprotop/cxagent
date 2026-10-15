using System.Text.Json.Nodes;

namespace CxAgent.UI;

/// <summary>
/// <c>/think &lt;level&gt; --save</c>: the instance's default level, written to config.json.
/// </summary>
public static class ThinkingConfigWriter
{
    /// <summary>
    /// Sets <c>providers.&lt;instance&gt;.thinking</c>, or removes it for null (the model's default).
    /// False when there is no such instance or the write failed — the session says so.
    ///
    /// <para>ONE KEY, THROUGH THE SAME WRITER THE THEME USES: the file is also edited by hand, and a
    /// save that rewrote the providers block from memory would undo whatever the user changed since
    /// launch.</para>
    /// </summary>
    public static bool TrySave(string configPath, string? instance, string? level)
    {
        if (instance is null || !File.Exists(configPath)) return false;
        var found = false;
        try
        {
            PluginConfigWriter.MutateRoot(configPath, root =>
            {
                if (root["providers"]?[instance] is not JsonObject provider) return;
                found = true;
                if (level is null) provider.Remove("thinking");
                else provider["thinking"] = level;
            });
            return found;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
