using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Abi;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Locks the ABI wire contract itself — schema round-trips, version-mismatch refusal, and malformed
/// JSON handling. Nothing here spawns a process or exercises a real host; that belongs to 9b/9c,
/// which are built against this contract rather than proving it.
/// </summary>
public class AbiCodecTests
{
    private const string WellFormedManifestJson = """
        {
          "pluginContract": 2,
          "name": "lsp-rust",
          "version": "1.0.0",
          "instructions": "Positions are 1-based.",
          "spawns": true,
          "tools": [
            { "name": "lsp_definition", "description": "Finds a symbol's declaration.",
              "inputSchema": { "type": "object", "properties": { "file": { "type": "string" } } },
              "gated": false }
          ]
        }
        """;

    // ---- version handshake ----

    [Fact]
    public void CurrentVersionPassesTheHandshake()
    {
        var result = AbiCodec.CheckVersion(PluginContract.Version);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void AFutureVersionIsRefusedByExactEquality_NotAFloor()
    {
        var result = AbiCodec.CheckVersion(PluginContract.Version + 1);

        Assert.False(result.IsSuccess);
        Assert.Contains((PluginContract.Version + 1).ToString(), result.Error);
        Assert.Contains(PluginContract.Version.ToString(), result.Error);
    }

    [Fact]
    public void AnOlderVersionIsAlsoRefused_NotOnlyNewerOnes()
    {
        // Exact equality cuts both ways: a v0 plugin is refused exactly as a v2 one is, because a
        // v1 host has no basis to assume a lower version is a strict subset of what it understands.
        var result = AbiCodec.CheckVersion(0);
        Assert.False(result.IsSuccess);
    }

    // ---- manifest: parse, version check, round trip to PluginManifest ----

    [Fact]
    public void ParsesAWellFormedManifestAndChecksItsEmbeddedVersion()
    {
        var result = AbiCodec.ParseManifest(WellFormedManifestJson);

        Assert.True(result.IsSuccess);
        Assert.Equal("lsp-rust", result.Value.Name);
        Assert.Equal("1.0.0", result.Value.Version);
        Assert.True(result.Value.Spawns);
        Assert.Single(result.Value.Tools);
        Assert.Equal("lsp_definition", result.Value.Tools[0].Name);
    }

    [Fact]
    public void ManifestWithMismatchedEmbeddedAbiVersionIsRefused()
    {
        // The handshake function and the manifest body both carry a version — see Abi/README.md,
        // "describe": deliberately redundant so the two can be checked against each other, not just
        // against the host's own constant.
        var json = WellFormedManifestJson.Replace("\"pluginContract\": 2", "\"pluginContract\": 99");

        var result = AbiCodec.ParseManifest(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("2", result.Error);
    }

    [Fact]
    public void ManifestMissingNameIsRefused()
    {
        var json = WellFormedManifestJson.Replace("\"name\": \"lsp-rust\",", "");
        var result = AbiCodec.ParseManifest(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("name", result.Error);
    }

    [Fact]
    public void MalformedManifestJsonIsRefusedWithABoundedPreview()
    {
        var result = AbiCodec.ParseManifest("{ not valid json");

        Assert.False(result.IsSuccess);
        Assert.Contains("not valid json", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManifestJsonNullIsRefused()
    {
        var result = AbiCodec.ParseManifest("null");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ToPluginManifestProducesTheSameShapeTheManagedLoaderWould()
    {
        // The registry must not be able to tell which loader produced a PluginManifest — see
        // AbiCodec's own doc. Round-trip an ABI manifest through Parse -> ToPluginManifest and
        // compare it against PluginManifest.Parse reading the equivalent sidecar shape directly.
        var abi = AbiCodec.ParseManifest(WellFormedManifestJson).Value;
        var translated = AbiCodec.ToPluginManifest(abi);

        var sidecarEquivalent = """
            {
              "name": "lsp-rust",
              "version": "1.0.0",
              "instructions": "Positions are 1-based.",
              "spawns": true,
              "tools": [
                { "name": "lsp_definition", "description": "Finds a symbol's declaration.",
                  "inputSchema": { "type": "object", "properties": { "file": { "type": "string" } } },
                  "gated": false }
              ]
            }
            """;
        var managed = CxAgent.Core.Plugins.PluginManifest.Parse(sidecarEquivalent).Manifest!;

        Assert.Equal(managed.Name, translated.Name);
        Assert.Equal(managed.Version, translated.Version);
        Assert.Equal(managed.Instructions, translated.Instructions);
        Assert.Equal(managed.Spawns, translated.Spawns);
        Assert.Equal(managed.Tools.Count, translated.Tools.Count);
        Assert.Equal(managed.Tools[0].Name, translated.Tools[0].Name);
        Assert.Equal(managed.Tools[0].Gated, translated.Tools[0].Gated);
    }

    // ---- context / invoke call: written shapes are never null where the header promises non-null ----

    [Fact]
    public void WriteContextProducesWorkingDirectoryAndSettings()
    {
        using var settingsDoc = JsonDocument.Parse("""{"server":"rust-analyzer"}""");
        var json = AbiCodec.WriteContext("/repo", settingsDoc.RootElement);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("/repo", doc.RootElement.GetProperty("workingDirectory").GetString());
        Assert.Equal("rust-analyzer", doc.RootElement.GetProperty("settings").GetProperty("server").GetString());
    }

    [Fact]
    public void WriteInvokeCallNeverEmitsNullArguments_EvenWhenNoneAreGiven()
    {
        // cxagent_plugin.h: "call_json ... never NULL, so a plugin may parse it unconditionally."
        var json = AbiCodec.WriteInvokeCall("lsp_diagnostics", new JobParameters());

        using var doc = JsonDocument.Parse(json);
        var args = doc.RootElement.GetProperty("arguments");
        Assert.Equal(JsonValueKind.Object, args.ValueKind);
    }

    [Fact]
    public void WriteInvokeCallCarriesToolNameAndArguments()
    {
        var call = new JobParameters(new Dictionary<string, object?> { ["file"] = "a.rs", ["line"] = 3 });
        var json = AbiCodec.WriteInvokeCall("lsp_definition", call);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("lsp_definition", doc.RootElement.GetProperty("toolName").GetString());
        Assert.Equal("a.rs", doc.RootElement.GetProperty("arguments").GetProperty("file").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("arguments").GetProperty("line").GetInt32());
    }

    // ---- result envelope: every row of Abi/README.md's failure-mode table ----

    [Fact]
    public void ParsesASuccessfulInvokeEnvelopeIntoAJobResult()
    {
        var json = """
            {
              "ok": true,
              "result": {
                "success": true, "exitCode": 0, "errorMessage": null, "permissionDenied": false,
                "decidedBy": null, "output": { "locations": [] }, "logFile": null, "durationMs": 42
              }
            }
            """;

        var envelope = AbiCodec.ParseEnvelope(json);
        Assert.True(envelope.IsSuccess);

        var result = AbiCodec.ToInvokeResult(envelope.Value);
        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Success);
        Assert.Equal(TimeSpan.FromMilliseconds(42), result.Value.Duration);
        Assert.True(result.Value.Output.ContainsKey("locations"));
    }

    [Fact]
    public void OkFalseAndJobResultSuccessFalseAreDistinctChannels()
    {
        // ok:false means the CALL failed (an ABI-level fault); ok:true + result.success:false means
        // the call completed and the TOOL failed on its own terms. See Abi/README.md, "The result
        // envelope": "ok and JobResult.Success are not the same bit."
        var callFailed = AbiCodec.ParseEnvelope("""{"ok":false,"error":"unknown tool 'x'"}""");
        Assert.True(callFailed.IsSuccess);
        Assert.False(callFailed.Value.Ok);
        var callFailedResult = AbiCodec.ToInvokeResult(callFailed.Value);
        Assert.False(callFailedResult.IsSuccess);
        Assert.Equal("unknown tool 'x'", callFailedResult.Error);

        var toolFailed = AbiCodec.ParseEnvelope(
            """{"ok":true,"result":{"success":false,"exitCode":0,"errorMessage":"not running.","permissionDenied":false,"decidedBy":null,"output":{},"logFile":null,"durationMs":0}}""");
        Assert.True(toolFailed.IsSuccess);
        Assert.True(toolFailed.Value.Ok);
        var toolFailedResult = AbiCodec.ToInvokeResult(toolFailed.Value);
        Assert.True(toolFailedResult.IsSuccess);
        Assert.False(toolFailedResult.Value.Success);
        Assert.Equal("not running.", toolFailedResult.Value.ErrorMessage);
    }

    [Fact]
    public void OkFalseWithNoErrorFieldGetsAGeneratedMessage()
    {
        var envelope = AbiCodec.ParseEnvelope("""{"ok":false}""");
        Assert.True(envelope.IsSuccess);
        Assert.False(string.IsNullOrEmpty(envelope.Value.Error));
    }

    [Fact]
    public void VoidOkTrueEnvelopeSucceedsForStartAndStop()
    {
        var envelope = AbiCodec.ParseEnvelope("""{"ok":true}""");
        Assert.True(envelope.IsSuccess);

        var result = AbiCodec.ToVoidResult(envelope.Value);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void InvokeEnvelopeThatIsOkTrueButMissingResultFails()
    {
        // "invoke always returns a JobResult" — an ok:true reply from invoke with no 'result' is a
        // plugin bug, not treated as a void success the way start/stop's bare ok:true is.
        var envelope = AbiCodec.ParseEnvelope("""{"ok":true}""");
        var result = AbiCodec.ToInvokeResult(envelope.Value);

        Assert.False(result.IsSuccess);
        Assert.Contains("result", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullOrEmptyEnvelopeIsRefused()
    {
        Assert.False(AbiCodec.ParseEnvelope(null).IsSuccess);
        Assert.False(AbiCodec.ParseEnvelope("").IsSuccess);
    }

    [Fact]
    public void InvalidJsonEnvelopeIsRefusedWithABoundedPreview()
    {
        var garbage = new string('x', 5000);
        var result = AbiCodec.ParseEnvelope("{" + garbage);

        Assert.False(result.IsSuccess);
        Assert.True(result.Error!.Length < garbage.Length);
    }

    [Fact]
    public void ValidJsonThatIsNotAnObjectIsRefused()
    {
        Assert.False(AbiCodec.ParseEnvelope("42").IsSuccess);
        Assert.False(AbiCodec.ParseEnvelope("[1,2,3]").IsSuccess);
        Assert.False(AbiCodec.ParseEnvelope("\"just a string\"").IsSuccess);
    }

    [Fact]
    public void ObjectWithoutOkFieldIsRefused()
    {
        var result = AbiCodec.ParseEnvelope("""{"result":{}}""");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void UnknownEnvelopeFieldsAreIgnored_TheForwardCompatibilitySeam()
    {
        var envelope = AbiCodec.ParseEnvelope("""{"ok":true,"diagnosticField":"future use"}""");
        Assert.True(envelope.IsSuccess);
        Assert.True(envelope.Value.Ok);
    }
}
