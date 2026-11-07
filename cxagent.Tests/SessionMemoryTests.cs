using CxAgent.Core.Llm;
using CxAgent.Core.Memory;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Memory as a session holds it: one index for the session's life, the same across a re-wire, retaken when the
/// conversation is cleared — and every change the model did not make itself said at the front of the next message.
/// </summary>
public sealed class SessionMemoryTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("sessmem-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("sessmem-proj-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private MemoryStore Store => new(_config, _project);

    private static ResolvedConfig WithMemory(ILlmProvider provider) =>
        ResolvedConfig.ForTesting(provider) is var r ? r with { Catalog = r.Catalog with { Memory = true } } : null!;

    private void Wire(Session session, ILlmProvider provider) =>
        SessionFactory.Wire(session, WithMemory(provider), new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);

    private static string System(CapturingProvider p, int i) => p.Requests[i].First(m => m.Role == "system").Content ?? "";

    private static string LastUser(CapturingProvider p, int i) => p.Requests[i].Last(m => m.Role == "user").Content ?? "";

    [Fact]
    public async Task A_rewire_keeps_the_index_its_predecessor_had()
    {
        Store.Write(new MemoryNote("before", "there at the start", MemoryScope.Global, "b"));
        var provider = new CapturingProvider();
        var session = new Session(_project);
        Wire(session, provider);
        await session.SendAndWait("one");

        Store.Write(new MemoryNote("after", "written mid-session", MemoryScope.Project, "b"));
        Wire(session, provider);   // what /model does
        await session.SendAndWait("two");

        Assert.Equal(System(provider, 0), System(provider, 1));
        Assert.DoesNotContain("- after", System(provider, 1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_made_elsewhere_is_said_at_the_front_of_the_next_message_once()
    {
        Store.Write(new MemoryNote("doomed", "about to go", MemoryScope.Global, "b"));
        Store.Write(new MemoryNote("edited", "will change", MemoryScope.Global, "b"));
        var provider = new CapturingProvider();
        var session = new Session(_project);
        Wire(session, provider);
        await session.SendAndWait("one");

        Store.Delete("doomed");
        Store.Write(new MemoryNote("edited", "has changed", MemoryScope.Global, "new body"));
        Store.Write(new MemoryNote("fresh", "from another session", MemoryScope.Project, "b"));
        await session.SendAndWait("two");
        await session.SendAndWait("three");

        var told = LastUser(provider, 1);
        Assert.Contains("'doomed' was deleted", told, StringComparison.Ordinal);
        Assert.Contains("'edited' was edited — has changed", told, StringComparison.Ordinal);
        Assert.Contains("'fresh' was added — from another session", told, StringComparison.Ordinal);
        Assert.EndsWith("two", told, StringComparison.Ordinal);
        Assert.Equal("three", LastUser(provider, 2));
        Assert.Equal(System(provider, 0), System(provider, 2));
    }

    [Fact]
    public async Task What_the_models_own_tool_wrote_is_not_told_back_to_it()
    {
        var provider = new CapturingProvider(new ToolCall
        {
            Id = "w", Name = "memory",
            Arguments = global::System.Text.Json.JsonSerializer.SerializeToElement(new { action = "write", scope = "project", name = "mine", description = "d", content = "c" }),
        });
        var session = new Session(_project);
        Wire(session, provider);
        await session.SendAndWait("remember");
        await session.SendAndWait("next");

        Assert.NotNull(Store.Find("mine"));
        Assert.Equal("next", LastUser(provider, provider.Requests.Count - 1));
    }

    [Fact]
    public async Task Clearing_the_conversation_retakes_the_index()
    {
        var provider = new CapturingProvider();
        var session = new Session(_project);
        Wire(session, provider);
        await session.SendAndWait("one");

        Store.Write(new MemoryNote("later", "written since", MemoryScope.Global, "b"));
        session.ClearContext();
        await session.SendAndWait("two");

        Assert.Contains("- later — written since", System(provider, 1), StringComparison.Ordinal);
        Assert.Equal("two", LastUser(provider, 1));
    }

    [Fact]
    public async Task Resuming_another_conversation_retakes_the_index()
    {
        // ANOTHER CONVERSATION'S PROMPT, and its cache is cold anyway: it must list the notes as they are now, and what
        // the left conversation's model knew must not stop this one being told.
        using var manager = SessionManager.Create(new CxAgent.Core.Storage.AppPaths(_config));
        var provider = new CapturingProvider();
        var session = manager.Open(_project, WithMemory(provider),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        await session.SendAndWait("one");
        Assert.Empty(session.MemoryIndex());

        Store.Write(new MemoryNote("since", "written before the resume", MemoryScope.Global, "b"));
        session.NoteAgentWroteMemory("since");
        manager.Resume(session, new CxAgent.Core.Storage.SessionSnapshot("other-conversation", [], 0, 0, DateTimeOffset.UtcNow, null), () => { });

        Assert.Contains(session.MemoryIndex(), n => n.Name == "since");
    }

    [Fact]
    public void Forgetting_asks_first_and_names_the_note()
    {
        Store.Write(new MemoryNote("deploy-target", "Where it deploys", MemoryScope.Project, "fly.io"));
        var session = new Session(_project);
        Wire(session, new CapturingProvider());

        var ask = session.AskForgetMemory("deploy-target");
        Assert.NotNull(ask.Note);
        Assert.Contains("Where it deploys", ask.Text, StringComparison.Ordinal);
        Assert.NotNull(Store.Find("deploy-target"));

        session.ForgetMemory("deploy-target");
        Assert.Null(Store.Find("deploy-target"));
        Assert.Null(session.AskForgetMemory("deploy-target").Note);
    }

    [Fact]
    public void Off_in_config_is_no_store()
    {
        var session = new Session(_project);
        SessionFactory.Wire(session, ResolvedConfig.ForTesting(new CapturingProvider()), new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        Assert.Null(session.Memory);
    }

    /// <summary>Plays its tool calls one per request, then answers; keeps every request's messages.</summary>
    private sealed class CapturingProvider(params ToolCall[] calls) : ILlmProvider
    {
        private readonly Queue<ToolCall> _calls = new(calls);
        public List<List<ChatMessage>> Requests { get; } = [];
        public string ProviderId => "capturing";
        public string DisplayName => "Capturing";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            Requests.Add([.. messages]);
            return Task.FromResult(_calls.TryDequeue(out var call)
                ? new LlmResponse { ToolCalls = [call], StopReason = "tool_use" }
                : new LlmResponse { Text = "ok", StopReason = "end_turn" });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, r.ToolCalls.FirstOrDefault(), true, null, r.StopReason);
        }
    }
}
