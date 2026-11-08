using System.Text.Json.Nodes;
using CxAgent.Core.Colleagues;

namespace CxAgent.Web;

/// <summary>The library as the browser's page reads it — the same <see cref="LibraryView"/> the terminal's tab draws.</summary>
public static class WebLibrary
{
    public static JsonArray Json(IReadOnlyList<LibraryEntry> entries) =>
        [.. entries.Select(e => (JsonNode)new JsonObject
        {
            ["name"] = e.Name,
            ["description"] = e.Description,
            ["knows"] = new JsonArray([.. e.Knows.Select(k => (JsonNode)JsonValue.Create(k)!)]),
            ["facts"] = new JsonObject
            {
                ["version"] = e.Facts.Version, ["rounds"] = e.Facts.Rounds, ["distilled"] = e.Facts.Distilled,
                ["skipped"] = e.Facts.Skipped, ["type"] = e.Facts.Type, ["model"] = e.Facts.Model,
            },
            ["experience"] = new JsonArray([.. e.Experience.Select(p => (JsonNode)new JsonObject
            {
                ["title"] = p.Title, ["markdown"] = p.Markdown,
            })]),
        })];
}
