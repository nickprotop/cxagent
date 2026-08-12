using System.Text.Json;
using CxAgent.Core.Mcp;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The MCP wire, driven against a REAL subprocess rather than a mock of one.
///
/// <para>The protocol is the risk here, not the shape of our types: newline-framed JSON-RPC over a
/// pipe, with a handshake, ids that must be matched, and two streams that deadlock if either is left
/// undrained. A fake that returns canned objects would pass while every one of those went wrong.</para>
///
/// <para>The scripted server is python3, which this machine has. Where it is missing these skip
/// rather than fail — a test that reports red on a machine simply lacking an interpreter teaches
/// nothing.</para>
/// </summary>
public class McpClientTests : IDisposable
{
    private readonly List<string> _scripts = new();

    public void Dispose()
    {
        foreach (var s in _scripts)
            try { File.Delete(s); } catch (Exception) { /* best effort */ }
    }

    private static bool HavePython =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Any(d => File.Exists(Path.Combine(d, "python3")));

    /// <summary>
    /// Writes a python server that answers `initialize`, `tools/list` and `tools/call` from a table,
    /// then returns the command to run it. Newline-framed JSON on stdout, exactly as MCP stdio
    /// transports do.
    /// </summary>
    private string[] Server(string toolsJson, string callResultJson, string? instructions = null,
        bool exitAfterInitialize = false, bool neverAnswerCalls = false,
        string? reportEnv = null, bool reportCwd = false, string? protocolVersion = null,
        string? failTool = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "mcpsrv-" + Guid.NewGuid().ToString("N") + ".py");
        _scripts.Add(path);

        var instr = instructions is null ? "None" : JsonSerializer.Serialize(instructions);

        // A PLAIN VERBATIM STRING with placeholder substitution, not interpolation. The python is
        // dense with braces — `{"tools": {}}` — and every raw-string interpolation form ends up
        // fighting them: doubling for the holes breaks the JSON, and not doubling breaks the C#.
        const string template = """
            import sys, json, os
            TOOLS = json.loads(r'''@@TOOLS@@''')
            # Reported back through the protocol so the assertion fails on a WRONG value, not just on
            # a dead process.
            REPORT = @@REPORT@@
            if REPORT is not None:
                for t in TOOLS:
                    t["description"] = t["description"].replace("@@VALUE@@", REPORT)
            CALL = json.loads(r'''@@CALL@@''')
            INSTRUCTIONS = @@INSTR@@
            for line in sys.stdin:
                line = line.strip()
                if not line:
                    continue
                msg = json.loads(line)
                method = msg.get("method")
                if method == "initialize":
                    result = {"protocolVersion": @@PROTO@@, "capabilities": {"tools": {}},
                              "serverInfo": {"name": "scripted", "version": "1"}}
                    if INSTRUCTIONS is not None:
                        result["instructions"] = INSTRUCTIONS
                    print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": result}), flush=True)
                    if @@EXIT@@:
                        sys.exit(0)
                elif method == "notifications/initialized":
                    pass
                elif method == "tools/list":
                    print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": {"tools": TOOLS}}), flush=True)
                elif method == "tools/call":
                    if @@HANG@@:
                        continue
                    # A NAMED TOOL FAILS, the rest succeed — so one call's error and another call's
                    # success can be observed on ONE connection, which is what a shared error field
                    # would confuse.
                    if @@FAILTOOL@@ is not None and @@FAILTOOL@@ in ("*", msg["params"]["name"]):
                        print(json.dumps({"jsonrpc": "2.0", "id": msg["id"],
                                          "error": {"code": -32000, "message": "boom in " + msg["params"]["name"]}}), flush=True)
                        continue
                    print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": CALL}), flush=True)
            """;

        // json.loads, NOT pasted as python literals. JSON's `true`/`false`/`null` are not python
        // names, so `CALL = {"isError": true}` dies with `NameError: name 'true' is not defined` —
        // the server then never answers and every test using it reports "no response to initialize",
        // which points the finger at the client rather than at the fixture.
        File.WriteAllText(path, template
            .Replace("@@TOOLS@@", toolsJson.Trim())
            .Replace("@@CALL@@", callResultJson.Trim())
            .Replace("@@INSTR@@", instr)
            .Replace("@@EXIT@@", exitAfterInitialize ? "True" : "False")
            .Replace("@@HANG@@", neverAnswerCalls ? "True" : "False")
            .Replace("@@FAILTOOL@@", failTool is null ? "None" : JsonSerializer.Serialize(failTool))
            .Replace("@@PROTO@@", JsonSerializer.Serialize(protocolVersion ?? "2025-06-18"))
            .Replace("@@REPORT@@",
                reportCwd ? "os.getcwd()"
                : reportEnv is not null ? $"os.environ.get({JsonSerializer.Serialize(reportEnv)}, \"\")"
                : "None"));

        return ["python3", path];
    }

    /// <summary>
    /// A server that reports an environment variable back as its tool DESCRIPTION.
    ///
    /// <para>Reporting it through the protocol rather than, say, exiting non-zero means the assertion
    /// fails if the value is missing OR wrong — not merely if the process died, which a dozen
    /// unrelated faults would also cause.</para>
    /// </summary>
    private string[] EnvReportingServer(string variable = "MY_SECRET") =>
        Server("""
            [{"name": "report", "description": "@@VALUE@@", "inputSchema": {"type": "object"}}]
            """, TextResult, reportEnv: variable);

    /// <summary>The same trick for the working directory.</summary>
    private string[] CwdReportingServer() =>
        Server("""
            [{"name": "report", "description": "@@VALUE@@", "inputSchema": {"type": "object"}}]
            """, TextResult, reportCwd: true);

    private const string OneTool = """
        [{"name": "echo", "description": "Echoes what it is given.",
          "inputSchema": {"type": "object",
                          "properties": {"text": {"type": "string", "description": "What to echo."}},
                          "required": ["text"]}}]
        """;

    private static readonly string TextResult = """
        {"content": [{"type": "text", "text": "hello from the server"}]}
        """;

    /// <summary>
    /// ONE CALL'S ERROR DOES NOT COLOUR ANOTHER'S. The failure text belongs to the call that
    /// failed, and to no other.
    ///
    /// <para>It used to live on a shared <c>Error</c> field: written by whichever call failed last,
    /// read into every later failure's message, and made sticky by <c>??=</c>. With one agent that
    /// was merely untidy — the last failure usually WAS yours. With two children on one server it
    /// became a wrong diagnosis: A times out, B's unrelated failure reports A's message, and the
    /// model reasons from an error belonging to another agent's call.</para>
    ///
    /// <para>CONCURRENTLY, which is the only way to see it. Run sequentially, the shared field is
    /// simply overwritten between calls and the wrong text never surfaces — a first version of this
    /// test did exactly that and passed against the bug. Two children on one server do NOT take
    /// turns, so the test must not either: here two calls that both fail are in flight at once, and
    /// each must report its OWN tool's name.</para>
    /// </summary>
    [Fact]
    public async Task ConcurrentCalls_EachReportTheirOwnError()
    {
        if (!HavePython) return;

        const string twoTools = """
            [{"name": "boom", "description": "Fails.", "inputSchema": {"type": "object"}},
             {"name": "bang", "description": "Also fails.", "inputSchema": {"type": "object"}}]
            """;

        // Both tools fail — the server echoes the tool NAME into its error, so a message carrying
        // the wrong name is proof one call read another's failure.
        await using var client = new McpClient("scripted",
            Server(twoTools, TextResult, failTool: "*"));
        await client.StartAsync(CancellationToken.None);

        var empty = JsonDocument.Parse("{}").RootElement;

        var a = client.CallToolAsync("boom", empty, CancellationToken.None);
        var b = client.CallToolAsync("bang", empty, CancellationToken.None);
        var results = await Task.WhenAll(a, b);

        Assert.Contains("boom in boom", results[0], StringComparison.Ordinal);
        Assert.Contains("boom in bang", results[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A FAILED CALL LEAVES THE CONNECTION USABLE, and leaves its status alone. `Error` is still the
    /// per-SERVER surface that /mcp reads — splitting the per-call text out of it must not start
    /// reporting a healthy server as broken because one call failed.
    /// </summary>
    [Fact]
    public async Task ACallFailing_DoesNotMarkTheConnectionBroken()
    {
        if (!HavePython) return;

        const string twoTools = """
            [{"name": "boom", "description": "Fails.", "inputSchema": {"type": "object"}},
             {"name": "fine", "description": "Works.", "inputSchema": {"type": "object"}}]
            """;

        await using var client = new McpClient("scripted",
            Server(twoTools, TextResult, failTool: "boom"));
        await client.StartAsync(CancellationToken.None);

        await client.CallToolAsync("boom", JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.Null(client.Error);
    }

    [Fact]
    public async Task StartAsync_ThenListTools_ReturnsTheServersTools()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted", Server(OneTool, TextResult));
        Assert.True(await client.StartAsync(CancellationToken.None));

        var tools = await client.ListToolsAsync(CancellationToken.None);

        var only = Assert.Single(tools);
        Assert.Equal("echo", only.Name);
    }

    /// <summary>
    /// EVERY TOOL'S DESCRIPTION SURVIVES. It is the only prose the model gets about what a tool does,
    /// and it comes from a server we did not write — so it is passed through verbatim rather than
    /// summarised or replaced with the tool name.
    /// </summary>
    [Fact]
    public async Task ListToolsAsync_KeepsEachToolsDescription()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted", Server(OneTool, TextResult));
        await client.StartAsync(CancellationToken.None);

        var only = Assert.Single(await client.ListToolsAsync(CancellationToken.None));

        Assert.Equal("Echoes what it is given.", only.Description);

        // And the per-parameter descriptions, which live inside the schema — so the schema must be
        // passed through whole rather than rebuilt from its property names.
        Assert.Contains("What to echo.", only.InputSchema.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SERVER'S OWN INSTRUCTIONS ARE CAPTURED. `initialize` may return prose about how to use the
    /// server — "paths are relative to the root you configured", "call list_tables before querying" —
    /// which no schema can express. Dropping it gives the model the shape of the tools and none of
    /// the guidance.
    /// </summary>
    [Fact]
    public async Task StartAsync_CapturesTheServersInstructions()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted",
            Server(OneTool, TextResult, instructions: "Always call echo twice."));
        await client.StartAsync(CancellationToken.None);

        Assert.Equal("Always call echo twice.", client.Instructions);
    }

    /// <summary>A server that sends none is the common case and must not produce an empty block.</summary>
    [Fact]
    public async Task StartAsync_WithNoInstructions_LeavesThemNull()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted", Server(OneTool, TextResult));
        await client.StartAsync(CancellationToken.None);

        Assert.Null(client.Instructions);
    }

    [Fact]
    public async Task CallToolAsync_ReturnsTheTextContent()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted", Server(OneTool, TextResult));
        await client.StartAsync(CancellationToken.None);

        var result = await client.CallToolAsync("echo", Args(new { text = "hi" }), CancellationToken.None);

        Assert.Contains("hello from the server", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// isError comes back as the error TEXT, not as a thrown exception. The model reads a tool result
    /// and can correct itself; an exception would end the turn over something it could have fixed.
    /// Matches opencode's convertTool.
    /// </summary>
    [Fact]
    public async Task CallToolAsync_OnAnErrorResult_ReturnsTheErrorText()
    {
        if (!HavePython) return;

        const string errorResult = """
            {"isError": true, "content": [{"type": "text", "text": "no such path"}]}
            """;
        await using var client = new McpClient("scripted", Server(OneTool, errorResult));
        await client.StartAsync(CancellationToken.None);

        var result = await client.CallToolAsync("echo", Args(new { text = "x" }), CancellationToken.None);

        Assert.Contains("no such path", result, StringComparison.Ordinal);
    }

    /// <summary>structuredContent with no text content is serialised, rather than coming back empty.</summary>
    [Fact]
    public async Task CallToolAsync_WithOnlyStructuredContent_SerialisesIt()
    {
        if (!HavePython) return;

        const string structured = """
            {"content": [], "structuredContent": {"rows": 3, "table": "users"}}
            """;
        await using var client = new McpClient("scripted", Server(OneTool, structured));
        await client.StartAsync(CancellationToken.None);

        var result = await client.CallToolAsync("echo", Args(new { text = "x" }), CancellationToken.None);

        Assert.Contains("users", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A SERVER'S OWN ENVIRONMENT REACHES IT. This is the spec's prescribed credential channel for
    /// stdio — <i>"retrieve credentials from the environment"</i> — so a config option that never
    /// arrives at the child would be worse than none: it would look like the key was supplied.
    ///
    /// <para>Proved by having the server report the variable back as its tool description, which
    /// means the assertion fails if the value is missing OR wrong, rather than merely if the process
    /// died.</para>
    /// </summary>
    [Fact]
    public async Task StartAsync_PassesTheConfiguredEnvironmentToTheServer()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted", EnvReportingServer(),
            environment: new Dictionary<string, string> { ["MY_SECRET"] = "hunter2" });
        await client.StartAsync(CancellationToken.None);

        var only = Assert.Single(await client.ListToolsAsync(CancellationToken.None));
        Assert.Equal("hunter2", only.Description);
    }

    /// <summary>The child still INHERITS our environment; the configured block is an overlay, not a
    /// replacement. Wiping it would break servers relying on PATH, HOME or a proxy variable.</summary>
    [Fact]
    public async Task StartAsync_StillInheritsTheParentEnvironment()
    {
        if (!HavePython) return;

        Environment.SetEnvironmentVariable("CXAGENT_TEST_INHERITED", "yes");
        try
        {
            await using var client = new McpClient("scripted",
                EnvReportingServer("CXAGENT_TEST_INHERITED"),
                environment: new Dictionary<string, string> { ["OTHER"] = "x" });
            await client.StartAsync(CancellationToken.None);

            var only = Assert.Single(await client.ListToolsAsync(CancellationToken.None));
            Assert.Equal("yes", only.Description);
        }
        finally { Environment.SetEnvironmentVariable("CXAGENT_TEST_INHERITED", null); }
    }

    /// <summary>
    /// The server starts in its configured directory. Servers that take a path argument resolve it
    /// relative to their cwd, so one launched from wherever cxagent happened to start reads a
    /// different tree than the user meant.
    /// </summary>
    [Fact]
    public async Task StartAsync_StartsTheServerInItsConfiguredDirectory()
    {
        if (!HavePython) return;

        var dir = Path.Combine(Path.GetTempPath(), "mcp-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var client = new McpClient("scripted", CwdReportingServer(), workingDirectory: dir);
            await client.StartAsync(CancellationToken.None);

            var only = Assert.Single(await client.ListToolsAsync(CancellationToken.None));

            // Resolved on both sides: macOS hands out /var paths that resolve to /private/var.
            Assert.Equal(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                         Path.GetFullPath(only.Description).TrimEnd(Path.DirectorySeparatorChar));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A command that does not exist is a dead server, not a crash.</summary>
    [Fact]
    public async Task StartAsync_WhenTheCommandDoesNotExist_ReturnsFalse()
    {
        await using var client = new McpClient("ghost", ["definitely-not-a-real-binary-xyz"]);

        Assert.False(await client.StartAsync(CancellationToken.None));
    }

    /// <summary>A server that dies mid-session fails its calls without taking anything else down.</summary>
    [Fact]
    public async Task CallToolAsync_AfterTheServerDies_ReturnsAnErrorRatherThanThrowing()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted",
            Server(OneTool, TextResult, exitAfterInitialize: true));
        await client.StartAsync(CancellationToken.None);

        var result = await client.CallToolAsync("echo", Args(new { text = "x" }), CancellationToken.None);

        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A server that never answers must not hang the turn loop for ever. The call fails; the server
    /// is left alone, because killing it would take every other tool on it down with one slow call.
    /// </summary>
    [Fact]
    public async Task CallToolAsync_WhenTheServerNeverAnswers_TimesOut()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted",
            Server(OneTool, TextResult, neverAnswerCalls: true), timeout: TimeSpan.FromMilliseconds(300));
        await client.StartAsync(CancellationToken.None);

        var result = await client.CallToolAsync("echo", Args(new { text = "x" }), CancellationToken.None);

        Assert.Contains("timed out", result, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Args(object value) =>
        JsonSerializer.SerializeToElement(value);
    // ---- protocol version ----------------------------------------------------------------------

    /// <summary>
    /// WE ASK FOR THE LATEST WE SUPPORT, not the oldest that exists. The spec: "the client MUST send
    /// a protocol version it supports. This SHOULD be the LATEST version supported by the client."
    ///
    /// <para>It used to send 2024-11-05 — the very first revision, which pairs with the deprecated
    /// HTTP+SSE transport. Asking low costs real capability and gains nothing, since a server that
    /// cannot meet us counter-offers anyway.</para>
    /// </summary>
    [Fact]
    public async Task StartAsync_AsksForTheLatestVersionWeSupport()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted", Server(OneTool, TextResult));
        await client.StartAsync(CancellationToken.None);

        Assert.Equal(McpProtocol.Latest, client.RequestedProtocolVersion);
    }

    /// <summary>
    /// A COUNTER-OFFER WE SUPPORT IS ACCEPTED AND USED. "If the server supports the requested version
    /// it MUST respond with the same version. Otherwise it MUST respond with another version it
    /// supports" — so an older server naming an older revision is the normal path, not a failure.
    /// </summary>
    [Fact]
    public async Task StartAsync_AcceptsACounterOfferWeSupport()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted",
            Server(OneTool, TextResult, protocolVersion: "2024-11-05"));

        Assert.True(await client.StartAsync(CancellationToken.None));
        Assert.Equal("2024-11-05", client.NegotiatedProtocolVersion);
    }

    /// <summary>
    /// A COUNTER-OFFER WE CANNOT SUPPORT DISCONNECTS, naming BOTH versions.
    ///
    /// <para>"If the client does not support the version in the server's response, it SHOULD
    /// disconnect." Carrying on would mean speaking a dialect the other end never agreed to — the
    /// failures from that surface later, as unexplained malformed replies, rather than here where the
    /// message can say exactly what happened.</para>
    /// </summary>
    [Fact]
    public async Task StartAsync_RejectsACounterOfferWeCannotSupport()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted",
            Server(OneTool, TextResult, protocolVersion: "2099-01-01"));

        Assert.False(await client.StartAsync(CancellationToken.None));
        Assert.NotNull(client.Error);
        Assert.Contains("2099-01-01", client.Error!, StringComparison.Ordinal);      // what it wanted
        Assert.Contains(McpProtocol.Latest, client.Error!, StringComparison.Ordinal); // what we offer
    }

    /// <summary>A server that names no version at all is taken at ours rather than refused — the
    /// field is technically required, but refusing over a missing string would break working servers
    /// for a purely cosmetic omission.</summary>
    [Fact]
    public async Task StartAsync_WithNoVersionInTheReply_KeepsOurs()
    {
        if (!HavePython) return;

        await using var client = new McpClient("scripted",
            Server(OneTool, TextResult, protocolVersion: ""));

        Assert.True(await client.StartAsync(CancellationToken.None));
        Assert.Equal(McpProtocol.Latest, client.NegotiatedProtocolVersion);
    }
}
