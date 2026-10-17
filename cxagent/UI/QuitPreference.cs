using System.Text.Json;

namespace CxAgent.UI;

/// <summary>
/// Whether Ctrl+Q asks "Quit cxagent?" when nothing is running — <c>confirmQuit</c> in <c>config.json</c>.
///
/// <para>READ FROM THE FILE AT EACH CTRL+Q rather than carried in the startup config. Nothing but the quit
/// key reads it, and a user who deletes the key to get the question back is answered on the next press,
/// not the next launch.</para>
/// </summary>
public static class QuitPreference
{
    private const string Key = "confirmQuit";

    /// <summary>
    /// True unless the file says <c>"confirmQuit": false</c>.
    ///
    /// <para>ANYTHING BUT A PLAIN FALSE ASKS, an unreadable file included. Skipping the question over a
    /// typo is the misreading that costs something; asking once too often costs a keystroke.</para>
    /// </summary>
    public static bool Asks(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            return !(doc.RootElement.ValueKind == JsonValueKind.Object
                     && doc.RootElement.TryGetProperty(Key, out var value)
                     && value.ValueKind == JsonValueKind.False);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    /// <summary>Records "Don't ask again". Throws when the file cannot be written; the caller says so.</summary>
    public static void StopAsking(string configPath)
        => PluginConfigWriter.MutateRoot(configPath, root => root[Key] = false);
}
