using System.Text.Json;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

public class PluginManifestTests
{
    [Fact]
    public void ParsesAWellFormedManifest()
    {
        var json = """
        {
          "name": "lsp-rust",
          "version": "1.0.0",
          "instructions": "These tools operate on an indexed workspace.",
          "spawns": true,
          "tools": [
            { "name": "lsp_definition", "description": "Jump to a symbol's definition.",
              "inputSchema": { "type": "object" }, "gated": false }
          ]
        }
        """;

        var result = PluginManifest.Parse(json);

        Assert.True(result.IsSuccess);
        var manifest = result.Manifest!;
        Assert.Equal("lsp-rust", manifest.Name);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("These tools operate on an indexed workspace.", manifest.Instructions);
        Assert.True(manifest.Spawns);
        var tool = Assert.Single(manifest.Tools);
        Assert.Equal("lsp_definition", tool.Name);
        Assert.Equal("Jump to a symbol's definition.", tool.Description);
        Assert.False(tool.Gated);
        Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
    }

    /// <summary>
    /// AN UNKNOWN KIND IS REFUSED BY NAME, not ignored. A plugin declaring `commands` against a
    /// build that services only tools is told so; silence would leave its author believing the
    /// declaration took effect.
    /// </summary>
    [Fact]
    public void AnUnknownKindIsReportedRatherThanIgnored()
    {
        var json = """
        {
          "name": "lsp-rust",
          "version": "1.0.0",
          "commands": [ { "name": "lsp.restart" } ],
          "tools": [
            { "name": "lsp_definition", "description": "Jump to a symbol's definition.",
              "inputSchema": { "type": "object" } }
          ]
        }
        """;

        var result = PluginManifest.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Contains("commands"));
    }

    /// <summary>
    /// A manifest declaring a kind this build does not service must not fail wholesale — its
    /// `tools` are still readable so a forward-compatible manifest degrades rather than dies. This
    /// test checks the parse still recovers the tool list even though it refuses to succeed.
    /// </summary>
    [Fact]
    public void AnUnknownKindStillParsesTheToolsItCarries()
    {
        var json = """
        {
          "name": "lsp-rust",
          "version": "1.0.0",
          "providers": [ { "name": "some-provider" } ],
          "tools": [
            { "name": "lsp_definition", "description": "Jump to a symbol's definition.",
              "inputSchema": { "type": "object" } }
          ]
        }
        """;

        var result = PluginManifest.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Manifest);
        var tool = Assert.Single(result.Manifest!.Tools);
        Assert.Equal("lsp_definition", tool.Name);
    }

    /// <summary>
    /// `gated` is optional and defaults to false — a plugin that never mentions it is not asking
    /// for permission on every call, since that would be a change of behaviour nobody opted into.
    /// </summary>
    [Fact]
    public void OmittedOptionalFieldsDefault()
    {
        var json = """
        {
          "name": "lsp-rust",
          "version": "1.0.0",
          "tools": [
            { "name": "lsp_definition", "description": "Jump to a symbol's definition.",
              "inputSchema": { "type": "object" } }
          ]
        }
        """;

        var result = PluginManifest.Parse(json);

        Assert.True(result.IsSuccess);
        var manifest = result.Manifest!;
        Assert.Null(manifest.Instructions);
        Assert.False(manifest.Spawns);
        Assert.False(manifest.Tools[0].Gated);
    }

    /// <summary>
    /// A tool with no `inputSchema` still parses — `ToolDefinition` carries a `JsonElement`
    /// unchanged, so the absence has to become a well-formed empty object rather than a parse
    /// failure or a default JsonElement that throws when a caller reads its ValueKind.
    /// </summary>
    [Fact]
    public void AToolWithNoSchemaParses()
    {
        var json = """
        {
          "name": "lsp-rust",
          "version": "1.0.0",
          "tools": [
            { "name": "lsp_definition", "description": "Jump to a symbol's definition." }
          ]
        }
        """;

        var result = PluginManifest.Parse(json);

        Assert.True(result.IsSuccess);
        var tool = result.Manifest!.Tools[0];
        Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
    }

    [Fact]
    public void RejectsInvalidJson()
    {
        var result = PluginManifest.Parse("{ not json");

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void RequiresNameAndVersion()
    {
        var json = """{ "tools": [] }""";

        var result = PluginManifest.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Contains("name"));
        Assert.Contains(result.Errors, e => e.Contains("version"));
    }
}
