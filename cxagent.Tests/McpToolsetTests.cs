using System.Text.Json;
using CxAgent.Core.Commands;
using CxAgent.Core.Mcp;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The seam where a third-party server's tools become tools the model can call.
///
/// <para>Driven against a FAKE server rather than a subprocess: the wire is <see cref="McpClient"/>'s
/// job and is covered by <see cref="McpClientTests"/> against a real one. What is at risk here is
/// naming, collision and dispatch — all of it pure logic, and testing it through a live pipe would
/// only make the failures slower and less specific.</para>
/// </summary>
public class McpToolsetTests
{
    /// <summary>
    /// A server standing in for a real one. Records what it was asked to call, so dispatch can be
    /// asserted on rather than inferred from a result string.
    /// </summary>
    private sealed class FakeServer(string name, params McpToolDef[] tools) : IMcpServer
    {
        public string Name { get; } = name;
        public string? Instructions { get; init; }
        public string? Error => null;
        public string Result { get; set; } = "ok";
        public string? CalledTool { get; private set; }

        public IReadOnlyList<McpToolDef> Tools { get; } = tools;

        public Task<string> CallToolAsync(string tool, JsonElement args, CancellationToken ct)
        {
            CalledTool = tool;
            return Task.FromResult(Result);
        }
    }

    private static McpToolDef Tool(string name, string description = "Does a thing.",
        string schema = """{"type":"object","properties":{}}""") =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone());

    private static ToolCall Call(string name) =>
        new() { Id = "1", Name = name, Arguments = JsonDocument.Parse("{}").RootElement.Clone() };

    /// <summary>Names are prefixed by server, so two servers offering "read" cannot collide.
    /// opencode's rule: sanitize(server) + "_" + sanitize(tool).</summary>
    [Fact]
    public void Definitions_PrefixEachToolWithItsServerName()
    {
        var toolset = new McpToolset([new FakeServer("files", Tool("read"))]);

        Assert.Equal("files_read", Assert.Single(toolset.Definitions()).Name);
    }

    /// <summary>
    /// Non-identifier characters are sanitised — a server named "my server" must not produce a tool
    /// name the provider will reject.
    ///
    /// <para>HYPHENS SURVIVE. Providers accept <c>[a-zA-Z0-9_-]</c>, and opencode's rule is exactly
    /// <c>value.replace(/[^a-zA-Z0-9_-]/g, "_")</c> (<c>mcp/catalog.ts:117</c>) — so "read-file" stays
    /// "read-file". Replacing them anyway would mangle the many real tool names that use them, for no
    /// gain.</para>
    /// </summary>
    [Fact]
    public void Definitions_SanitiseNamesTheProviderWouldReject()
    {
        var toolset = new McpToolset([new FakeServer("my server!", Tool("read-file"))]);

        Assert.Equal("my_server__read-file", Assert.Single(toolset.Definitions()).Name);
    }

    /// <summary>The schema is passed through as the server gave it.</summary>
    [Fact]
    public void Definitions_PassTheServersSchemaThrough()
    {
        var server = new FakeServer("db", Tool("query",
            schema: """{"type":"object","properties":{"sql":{"type":"string"}},"required":["sql"]}"""));

        var schema = Assert.Single(new McpToolset([server]).Definitions()).InputSchema.GetRawText();

        Assert.Contains("\"sql\"", schema, StringComparison.Ordinal);
        Assert.Contains("required", schema, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE DESCRIPTION, which is the half a schema cannot carry. Our own tools set one, and each
    /// parameter carries its own; a third-party tool arriving with an empty description would be
    /// strictly worse off than a built-in for no reason.
    /// </summary>
    [Fact]
    public void Definitions_CarryEachToolsDescriptionIntoTheToolDefinition()
    {
        var server = new FakeServer("db", Tool("query", description: "Runs read-only SQL."));

        Assert.Equal("Runs read-only SQL.", Assert.Single(new McpToolset([server]).Definitions()).Description);
    }

    /// <summary>Per-parameter descriptions survive too — they live inside the server's schema, so it
    /// must be passed through whole rather than rebuilt from its property names.</summary>
    [Fact]
    public void Definitions_KeepPerParameterDescriptionsInsideTheSchema()
    {
        var server = new FakeServer("db", Tool("query",
            schema: """{"type":"object","properties":{"sql":{"type":"string","description":"The query to run."}}}"""));

        Assert.Contains("The query to run.",
            Assert.Single(new McpToolset([server]).Definitions()).InputSchema.GetRawText(),
            StringComparison.Ordinal);
    }

    /// <summary>A disabled server offers nothing. It is configured, so it is not an error — it is off.</summary>
    [Fact]
    public void Definitions_SkipAServerThatIsNotConnected()
    {
        Assert.Empty(new McpToolset([]).Definitions());
    }

    /// <summary>The call reaches the right server under the tool's ORIGINAL name — the prefix is ours,
    /// and a server asked to run "files_read" would rightly say it has no such tool.</summary>
    [Fact]
    public async Task TryInvokeAsync_CallsTheServerWithTheUnprefixedName()
    {
        var server = new FakeServer("files", Tool("read")) { Result = "file contents" };
        var toolset = new McpToolset([server], new RecordingGate(allow: true));

        var result = await toolset.TryInvokeAsync(Call("files_read"), CancellationToken.None);

        Assert.Equal("file contents", result);
        Assert.Equal("read", server.CalledTool);
    }

    /// <summary>
    /// AN MCP PROMPT SAYS WHO IS ASKING. The attribution work reached every other
    /// request-construction site and missed this one, because MCP takes no JobContext to copy it
    /// from — the executor path gets it via <c>context.Requester</c>.
    ///
    /// <para>With one child at a time this is merely unhelpful: the user knows what they started.
    /// With two children up, a prompt to approve third-party code on their machine has no answer to
    /// "which of them wants this?"</para>
    /// </summary>
    [Fact]
    public async Task TryInvokeAsync_NamesTheRequester_WhenAChildIsAsking()
    {
        var server = new FakeServer("files", Tool("read")) { Result = "file contents" };
        var gate = new RecordingGate(allow: true);
        var toolset = new McpToolset([server], gate);

        await toolset.TryInvokeAsync(Call("files_read"), CancellationToken.None,
            requester: "analyse the config");

        Assert.Equal("analyse the config", Assert.Single(gate.Asked).Requester);
    }

    /// <summary>The session's own agent has no label, and must not acquire a misleading one — null
    /// is what "the agent you are talking to" looks like everywhere else.</summary>
    [Fact]
    public async Task TryInvokeAsync_LeavesTheRequesterNull_ForTheSessionsOwnAgent()
    {
        var server = new FakeServer("files", Tool("read")) { Result = "file contents" };
        var gate = new RecordingGate(allow: true);

        await new McpToolset([server], gate)
            .TryInvokeAsync(Call("files_read"), CancellationToken.None);

        Assert.Null(Assert.Single(gate.Asked).Requester);
    }

    /// <summary>A name no server owns is a MISS, not an error — the caller falls through to the
    /// built-ins, whose "no such tool" text stays the single fallback for a name nobody owns.</summary>
    [Fact]
    public async Task TryInvokeAsync_AnUnknownName_ReturnsNullSoTheCallerFallsThrough()
    {
        var toolset = new McpToolset([new FakeServer("files", Tool("read"))]);

        Assert.Null(await toolset.TryInvokeAsync(Call("read_file"), CancellationToken.None));
    }

    /// <summary>An MCP result is truncated like any other, or one call fills the context window.</summary>
    [Fact]
    public async Task TryInvokeAsync_TruncatesAHugeMcpResult()
    {
        var server = new FakeServer("files", Tool("read"))
        {
            Result = new string('x', CxAgent.Core.Jobs.ToolBindings.MaxToolResultChars + 10_000),
        };

        var result = await new McpToolset([server], new RecordingGate(allow: true))
            .TryInvokeAsync(Call("files_read"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Length <= CxAgent.Core.Jobs.ToolBindings.MaxToolResultChars,
            $"an MCP result escaped the cap at {result.Length} chars");
        Assert.Contains("elided", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A COMPOSED NAME THAT SHADOWS A BUILT-IN IS DROPPED.
    ///
    /// <para>Prefixing gives <c>server_tool</c>, so a server named "read" offering "file" composes to
    /// <c>read_file</c> — ours. Dispatch order would protect the CALL, but the tools array handed to
    /// the model would still carry two <c>read_file</c> entries, which providers reject outright.</para>
    /// </summary>
    [Fact]
    public void Definitions_SkipAToolThatWouldShadowABuiltIn()
    {
        var toolset = new McpToolset([new FakeServer("read", Tool("file"))]);

        Assert.Empty(toolset.Definitions());
        Assert.Contains(toolset.Warnings, w => w.Contains("read_file", StringComparison.Ordinal));
    }

    /// <summary>
    /// AND TWO SERVERS COLLIDING WITH EACH OTHER. "my server" and "my_server" both sanitise to
    /// <c>my_server</c>, a collision that exists only AFTER sanitisation — so no config-level
    /// uniqueness check can see it. First one wins; the loser is named in a warning.
    /// </summary>
    [Fact]
    public void Definitions_SkipASecondServersCollidingToolAndSaysSo()
    {
        var toolset = new McpToolset([
            new FakeServer("my server", Tool("go")),
            new FakeServer("my_server", Tool("go")),
        ]);

        Assert.Single(toolset.Definitions());
        Assert.Contains(toolset.Warnings, w => w.Contains("my_server_go", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE UNKNOWN-TOOL MESSAGE NAMES MCP TOOLS TOO.
    ///
    /// <para>Otherwise a model that mis-typed one is told the available tools are the built-ins,
    /// hiding every tool it can actually reach. Worst after a RESUME: the restored context is
    /// replayed verbatim, so a model that used <c>files_read</c> last session calls it again — and if
    /// that server is gone it gets a list omitting the servers still running.</para>
    /// </summary>
    [Fact]
    public async Task InvokeAsync_UnknownTool_ListsMcpToolsAsWellAsBuiltIns()
    {
        var toolset = new McpToolset([new FakeServer("files", Tool("read"))]);

        var result = (await CxAgent.Core.Jobs.ToolBindings.InvokeAsync(
            Call("totally_made_up"), Enum.GetValues<CxAgent.Core.Llm.BuiltinTool>(),
            CxAgent.Core.Jobs.JobRegistry.CreateWithBuiltins(),
            new TestJobContext(), CancellationToken.None, toolset.Names())).Text;

        Assert.Contains("no such tool", result, StringComparison.Ordinal);
        Assert.Contains("files_read", result, StringComparison.Ordinal);   // the MCP tool is offered
        Assert.Contains("read_file", result, StringComparison.Ordinal);    // and the built-ins remain
    }

    // ---- the permission gate -------------------------------------------------------------------

    /// <summary>A gate that records what it was asked and answers a fixed way.</summary>
    private sealed class RecordingGate(bool allow) : CxAgent.Core.Permissions.IPermissionGate
    {
        public List<CxAgent.Core.Permissions.PermissionRequest> Asked { get; } = [];

        public Task<CxAgent.Core.Permissions.PermissionOutcome> RequestAsync(
            CxAgent.Core.Permissions.PermissionRequest request, CancellationToken ct)
        {
            Asked.Add(request);
            return Task.FromResult(allow
                ? CxAgent.Core.Permissions.PermissionOutcome.Allow
                : CxAgent.Core.Permissions.PermissionOutcome.ByUser);
        }
    }

    /// <summary>
    /// EVERY MCP CALL IS GATED. The built-in file, shell and http executors are wrapped in
    /// <c>PermissionGatedExecutor</c>; third-party code running on the user's machine with the user's
    /// credentials gets no weaker treatment.
    /// </summary>
    [Fact]
    public async Task AnMcpCall_IsRefused_WhenTheGateSaysNo()
    {
        var server = new FakeServer("files", Tool("read")) { Result = "SECRET CONTENTS" };
        var gate = new RecordingGate(allow: false);

        var result = await new McpToolset([server], gate).TryInvokeAsync(Call("files_read"), CancellationToken.None);

        Assert.Null(server.CalledTool);                                        // never reached the server
        Assert.DoesNotContain("SECRET", result!, StringComparison.Ordinal);
    }

    /// <summary>A denial comes back as a refusal the MODEL can read, not an exception — the same
    /// contract the built-in tools hold, and it tells the model not to retry.</summary>
    [Fact]
    public async Task ADeniedMcpCall_ReadsAsARefusal_NotACrash()
    {
        var toolset = new McpToolset([new FakeServer("files", Tool("read"))], new RecordingGate(allow: false));

        var result = await toolset.TryInvokeAsync(Call("files_read"), CancellationToken.None);

        Assert.Contains("permission denied", result!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not retry", result!, StringComparison.Ordinal);
    }

    /// <summary>An allowed call runs, so the gate is a gate and not a wall.</summary>
    [Fact]
    public async Task AnAllowedMcpCall_Runs()
    {
        var server = new FakeServer("files", Tool("read")) { Result = "contents" };

        var result = await new McpToolset([server], new RecordingGate(allow: true))
            .TryInvokeAsync(Call("files_read"), CancellationToken.None);

        Assert.Equal("contents", result);
        Assert.Equal("read", server.CalledTool);
    }

    /// <summary>
    /// "Always" remembers the SERVER AND TOOL, not the arguments. A file rule keys on a path because
    /// a path is the thing being risked; an MCP tool's arguments are a schema we did not write and
    /// cannot interpret, so the tool itself is the only subject we can honestly generalise.
    /// </summary>
    [Fact]
    public async Task TheAlwaysRule_IsTheServerAndToolName()
    {
        var gate = new RecordingGate(allow: true);

        await new McpToolset([new FakeServer("files", Tool("read"))], gate)
            .TryInvokeAsync(Call("files_read"), CancellationToken.None);

        var asked = Assert.Single(gate.Asked);
        Assert.Equal(CxAgent.Core.Permissions.PermissionKind.Mcp, asked.Kind);
        Assert.Equal("mcp:files_read", asked.AlwaysRule);
    }

    /// <summary>
    /// The prompt shows the server, the tool AND the arguments. The user is approving a call into
    /// code we cannot inspect; showing less than the whole call is not honest.
    /// </summary>
    [Fact]
    public async Task ThePrompt_ShowsTheServerTheToolAndTheArguments()
    {
        var gate = new RecordingGate(allow: true);
        var call = new ToolCall
        {
            Id = "1",
            Name = "files_read",
            Arguments = JsonDocument.Parse("""{"path":"/etc/passwd"}""").RootElement.Clone(),
        };

        await new McpToolset([new FakeServer("files", Tool("read"))], gate)
            .TryInvokeAsync(call, CancellationToken.None);

        var display = Assert.Single(gate.Asked).Display;
        Assert.Contains("files", display, StringComparison.Ordinal);
        Assert.Contains("read", display, StringComparison.Ordinal);
        Assert.Contains("/etc/passwd", display, StringComparison.Ordinal);
    }

    /// <summary>
    /// NO GATE MEANS NO CALL, not a free pass.
    ///
    /// <para>The gate is optional in the constructor so existing call sites compile, which is exactly
    /// the shape that turns into a silent bypass. A toolset built without one refuses rather than
    /// allowing — the same reasoning as the headless default gate that can never say yes.</para>
    /// </summary>
    [Fact]
    public async Task WithNoGateConfigured_TheCallIsRefusedRatherThanAllowed()
    {
        var server = new FakeServer("files", Tool("read")) { Result = "SECRET" };

        var result = await new McpToolset([server]).TryInvokeAsync(Call("files_read"), CancellationToken.None);

        Assert.Null(server.CalledTool);
        Assert.Contains("permission", result!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The server's own usage prose is carried, for the system prompt to show.</summary>
    [Fact]
    public void Instructions_AreCarriedPerServer()
    {
        var toolset = new McpToolset([
            new FakeServer("db", Tool("query")) { Instructions = "Call list_tables first." },
            new FakeServer("files", Tool("read")),
        ]);

        var only = Assert.Single(toolset.InstructionsByServer());
        Assert.Equal("db", only.Key);
        Assert.Equal("Call list_tables first.", only.Value);
    }

    [Fact]
    public async Task AnMcpCall_ThroughTheREALGate_IsNotRefusedForWantOfAPolicy()
    {
        // REPRODUCTION. Every other test here uses RecordingGate, which allows unconditionally and
        // never looks at Policy — so nothing exercised PermissionDecider, where the check lives.
        var dir = Directory.CreateTempSubdirectory("mcppol").FullName;
        var rules = new PermissionRulesStore(new AppPaths(dir));
        // WithPrompt, NOT ForTesting: ForTesting sets StampForTesting, which patches the missing
        // policy and hides the exact production defect this test is for.
        var notices = new List<Message>();
        var gate = PermissionDecider.WithPrompt(rules, notices.Add,
            (_, _, _) => Task.FromResult(PermissionChoice.Once));

        var toolset = new McpToolset([new FakeServer("ctx", Tool("query"))], gate);

        var result = await toolset.TryInvokeAsync(
            new ToolCall { Name = "ctx_query", Arguments = JsonDocument.Parse("{}").RootElement.Clone() },
            CancellationToken.None,
            requester: null,
            policy: new PermissionPolicy(dir, rules, EditMode.AcceptEdits));

        // THE PROMPT HOOK SAID YES, so the only thing that can deny is the missing policy.
        Assert.DoesNotContain(notices, n => n.Text.Contains("carried no session policy"));
        Assert.DoesNotContain("permission denied", result ?? "");
    }
}
