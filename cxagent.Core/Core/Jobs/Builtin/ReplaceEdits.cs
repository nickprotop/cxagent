using System.Text.Json;
using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs.Builtin;

/// <summary>One exact replacement inside a file: the text that is there, and the text it becomes.</summary>
internal sealed record ReplaceEdit(string Pattern, string Replacement);

/// <summary>
/// The edits a replace call carries, and whether they came as a list.
/// </summary>
/// <param name="Edits">In the order they are applied, each to the result of the one before.</param>
/// <param name="FromList">
/// True when the call sent <c>edits</c>. A refusal then names the edit by number; a flat call is
/// answered in exactly the words it always was, since nothing in it needs numbering.
/// </param>
internal sealed record ReplaceEditList(IReadOnlyList<ReplaceEdit> Edits, bool FromList);

/// <summary>
/// Reads replace_in_file's edits out of a call's arguments — the ONE reader, used by the executor to
/// validate and apply them and by the export to diff them, so the two cannot disagree about which
/// shapes a call may take.
/// </summary>
internal static class ReplaceEdits
{
    /// <summary>
    /// The edits in <paramref name="parameters"/>, or an <see cref="InvalidOperationException"/> whose
    /// message is written for the model to act on.
    ///
    /// <para>TWO SHAPES ARE READ, ONE IS ADVERTISED. The schema offers only <c>edits</c>. The flat
    /// <c>pattern</c>/<c>replacement</c> pair is still accepted as a list of one because resumed
    /// conversations are full of it, and a model copies its own history more readily than it reads a
    /// schema; refusing it would cost a turn per file in every resumed session, worst on the small
    /// local models this list exists for. A call carrying both is refused: which one was meant is a
    /// guess.</para>
    ///
    /// <para>A LIST SENT AS A STRING is parsed. Local models often send a nested argument as a string
    /// holding its JSON, and the list inside is exactly what was meant.</para>
    /// </summary>
    public static ReplaceEditList Read(JobParameters parameters)
    {
        parameters.Values.TryGetValue("edits", out var raw);
        var hasList = raw is not null && raw is not JsonElement { ValueKind: JsonValueKind.Null };
        // JSON NULL IS ABSENT, for these keys as for `edits`: a model that fills every field it was shown
        // with null has not sent a second shape.
        var hasFlat = Present(parameters, "pattern") || Present(parameters, "replacement");

        if (hasList && hasFlat)
            throw new InvalidOperationException(
                "Send the changes either as 'edits' or as a top-level 'pattern' and 'replacement', not "
                + "both — put every change in 'edits'. Nothing was written.");

        if (!hasList)
        {
            if (!hasFlat)
                throw new InvalidOperationException("'edits' is required: a list of {pattern, replacement}.");
            if (string.IsNullOrEmpty(parameters.Get("pattern", "")))
                throw new InvalidOperationException("'pattern' is required for action 'replace'.");
            if (parameters.Get<string?>("replacement", null) is not { } flatReplacement)
                throw new InvalidOperationException("'replacement' is required for action 'replace'.");
            return new([new ReplaceEdit(parameters.Get<string>("pattern"), flatReplacement)], FromList: false);
        }

        var list = AsArray(raw!);
        if (list.GetArrayLength() == 0)
            throw new InvalidOperationException(
                "'edits' is empty. Send at least one {pattern, replacement}.");

        var edits = new List<ReplaceEdit>();
        var n = 0;
        foreach (var item in list.EnumerateArray())
        {
            n++;
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"edit {n}: must be an object with 'pattern' and 'replacement'.");
            if (!item.TryGetProperty("pattern", out var p) || p.ValueKind != JsonValueKind.String
                || p.GetString() is not { Length: > 0 } pattern)
                throw new InvalidOperationException($"edit {n}: 'pattern' is required.");
            // AN EMPTY REPLACEMENT IS A DELETION, so only a missing one is refused.
            if (!item.TryGetProperty("replacement", out var r) || r.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"edit {n}: 'replacement' is required (an empty string deletes).");
            edits.Add(new ReplaceEdit(pattern, r.GetString()!));
        }
        return new(edits, FromList: true);
    }

    private static bool Present(JobParameters parameters, string key) =>
        parameters.Values.TryGetValue(key, out var v) && v is not null && v is not JsonElement { ValueKind: JsonValueKind.Null };

    private static JsonElement AsArray(object raw)
    {
        var element = raw is JsonElement e ? e : JsonSerializer.SerializeToElement(raw);
        if (element.ValueKind == JsonValueKind.String)
        {
            try { element = JsonDocument.Parse(element.GetString() ?? "").RootElement.Clone(); }
            catch (JsonException)
            {
                throw new InvalidOperationException(
                    "'edits' must be a list of {pattern, replacement}; the string sent is not JSON.");
            }
        }
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("'edits' must be a list of {pattern, replacement}.");
        return element;
    }
}
