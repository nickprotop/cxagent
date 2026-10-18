using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Export;

namespace CxAgent.UI;

/// <summary>The export dialog's choices, as the last export left them.</summary>
/// <param name="Parts">What was included.</param>
/// <param name="ToClipboard">Copied rather than written to a file.</param>
/// <param name="OpenWhenWritten">The file was opened once written.</param>
public sealed record ExportChoices(ExportParts Parts, bool ToClipboard, bool OpenWhenWritten)
{
    /// <summary>What the dialog opens with before anything was remembered.</summary>
    public static ExportChoices Default { get; } = new(ExportOptions.Readable, false, false);
}

/// <summary>
/// The export dialog's remembered choices — <c>export</c> in <c>config.json</c>.
///
/// <para>REMEMBERED BECAUSE EXPORTS REPEAT: someone who exports full to the clipboard for another agent does it the
/// same way every time, and should not set it up again. Read at each opening, as <see cref="QuitPreference"/> is.</para>
/// </summary>
public static class ExportPreference
{
    private const string Key = "export";

    /// <summary>The remembered choices, or the defaults — a missing or unreadable file included.</summary>
    public static ExportChoices Read(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return ExportChoices.Default;
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(Key, out var saved) || saved.ValueKind != JsonValueKind.Object)
                return ExportChoices.Default;

            return new ExportChoices(
                saved.TryGetProperty("parts", out var parts) && parts.TryGetInt32(out var bits)
                    ? (ExportParts)bits & ExportOptions.Full
                    : ExportChoices.Default.Parts,
                saved.TryGetProperty("clipboard", out var clipboard) && clipboard.ValueKind == JsonValueKind.True,
                saved.TryGetProperty("open", out var open) && open.ValueKind == JsonValueKind.True);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return ExportChoices.Default;
        }
    }

    /// <summary>Remembers <paramref name="choices"/>. Throws when the file cannot be written; the caller says so.</summary>
    public static void Save(string configPath, ExportChoices choices) =>
        PluginConfigWriter.MutateRoot(configPath, root => root[Key] = new JsonObject
        {
            ["parts"] = (int)choices.Parts,
            ["clipboard"] = choices.ToClipboard,
            ["open"] = choices.OpenWhenWritten,
        });
}
