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
        bool exitAfterInitialize = false, bool neverAnswerCalls = false)
    {
        var path = Path.Combine(Path.GetTempPath(), "mcpsrv-" + Guid.NewGuid().ToString("N") + ".py");
        _scripts.Add(path);

        var instr = instructions is null ? "None" : JsonSerializer.Serialize(instructions);

        // A PLAIN VERBATIM STRING with placeholder substitution, not interpolation. The python is
        // dense with braces — `{"tools": {}}` — and every raw-string interpolation form ends up
        // fighting them: doubling for the holes breaks the JSON, and not doubling breaks the C#.
        const string template = """
            import sys, json
            TOOLS = json.loads(r'''@@TOOLS@@''')
            CALL = json.loads(r'''@@CALL@@''')
            INSTRUCTIONS = @@INSTR@@
            for line in sys.stdin:
                line = line.strip()
                if not line:
                    continue
                msg = json.loads(line)
                method = msg.get("method")
                if method == "initialize":
                    result = {"protocolVersion": "2024-11-05", "capabilities": {"tools": {}},
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
            .Replace("@@HANG@@", neverAnswerCalls ? "True" : "False"));

        return ["python3", path];
    }

    private const string OneTool = """
        [{"name": "echo", "description": "Echoes what it is given.",
          "inputSchema": {"type": "object",
                          "properties": {"text": {"type": "string", "description": "What to echo."}},
                          "required": ["text"]}}]
        """;

    private static readonly string TextResult = """
        {"content": [{"type": "text", "text": "hello from the server"}]}
        """;

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
}
