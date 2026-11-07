using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Memory;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Memory as an agent has it: the tool offered when there is a store, the index in the system prompt, and the
/// prompt's bytes kept steady while notes are written — a changed prefix re-sends the whole conversation uncached.
/// </summary>
public sealed class MemoryAgentTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("memagent-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("memagent-proj-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private MemoryStore Store => new(_config, _project);

    private static AgentSetup Setup(ScriptedProvider provider, MemoryStore? store, bool subAgent = false,
        Func<IReadOnlyList<MemoryNote>>? index = null)
    {
        var setup = TestAgents.Setup(provider, new(new RecordingSink(), new NullJobPanel()), 5);
        return setup with
        {
            Identity = setup.Identity with { IsSubAgent = subAgent },
            Tools = setup.Tools with { Memory = store },
            Brief = setup.Brief with { MemoryIndex = index },
        };
    }

    [Fact]
    public async Task With_a_store_the_tool_is_offered_and_the_index_is_in_the_prompt()
    {
        var store = Store;
        store.Write(new MemoryNote("prefers-tabs", "Indentation preference", MemoryScope.Global, "Tabs."));
        var provider = new ScriptedProvider();

        await new Agent(Setup(provider, store)).SendAsync("hi", CancellationToken.None);

        Assert.Contains("memory", provider.Tools[0]);
        Assert.Contains("- prefers-tabs — Indentation preference", provider.Systems[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Tabs.", provider.Systems[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_store_there_is_no_tool_and_no_section()
    {
        var provider = new ScriptedProvider();
        await new Agent(Setup(provider, null)).SendAsync("hi", CancellationToken.None);

        Assert.DoesNotContain("memory", provider.Tools[0]);
        Assert.DoesNotContain("# Memory", provider.Systems[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_written_mid_session_leaves_the_system_prompt_as_it_was()
    {
        var provider = new ScriptedProvider(Write("new-fact", "project"));
        await new Agent(Setup(provider, Store)).SendAsync("remember this", CancellationToken.None);

        Assert.NotNull(Store.Find("new-fact"));
        Assert.Equal(2, provider.Systems.Count);
        Assert.Equal(provider.Systems[0], provider.Systems[1]);
    }

    [Fact]
    public async Task The_index_comes_from_the_snapshot_it_is_handed_not_from_disk()
    {
        // THE SESSION'S SNAPSHOT: an agent rebuilt by /model reads the same index its predecessor did, whatever was
        // written since — the prefix of a long conversation is not re-sent for a note.
        var store = Store;
        store.Write(new MemoryNote("on-disk", "d", MemoryScope.Global, "b"));
        var provider = new ScriptedProvider();
        IReadOnlyList<MemoryNote> snapshot = [new MemoryNote("from-snapshot", "d", MemoryScope.Global, "")];

        await new Agent(Setup(provider, store, index: () => snapshot)).SendAsync("hi", CancellationToken.None);

        Assert.Contains("from-snapshot", provider.Systems[0], StringComparison.Ordinal);
        Assert.DoesNotContain("on-disk", provider.Systems[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sub_agent_is_told_it_reads_only_and_its_write_is_refused()
    {
        var provider = new ScriptedProvider(Write("child-fact", "global"));
        await new Agent(Setup(provider, Store, subAgent: true)).SendAsync("go", CancellationToken.None);

        Assert.Contains("memory", provider.Tools[0]);
        Assert.Contains("cannot write", provider.Systems[0], StringComparison.Ordinal);
        Assert.Null(Store.Find("child-fact"));
    }

    [Fact]
    public async Task A_selection_that_removes_memory_removes_the_section_too()
    {
        var provider = new ScriptedProvider();
        var setup = Setup(provider, Store);
        setup = setup with { Tools = setup.Tools with { Selection = new CxAgent.Core.Jobs.ToolSelection(["inherited", CxAgent.Core.Jobs.Tool.Not.Memory]) } };

        await new Agent(setup).SendAsync("hi", CancellationToken.None);

        Assert.DoesNotContain("memory", provider.Tools[0]);
        Assert.DoesNotContain("# Memory", provider.Systems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_memory_row_names_what_it_did_and_to_which_note()
    {
        var call = Write("deploy-target", "project");
        Assert.Equal("memory · write · deploy-target", Agent.DescribeCall(call, null));
    }

    private static ToolCall Write(string name, string scope) => new()
    {
        Id = "w1", Name = "memory",
        Arguments = JsonSerializer.SerializeToElement(new { action = "write", scope, name, description = "d", content = "c" }),
    };

    /// <summary>Plays the given tool calls one per request, then answers; records each request's system text and tools.</summary>
    private sealed class ScriptedProvider(params ToolCall[] calls) : ILlmProvider
    {
        private readonly Queue<ToolCall> _calls = new(calls);
        public List<string> Systems { get; } = [];
        public List<List<string>> Tools { get; } = [];
        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            Systems.Add(messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "");
            Tools.Add([.. (tools ?? []).Select(t => t.Name)]);
            return Task.FromResult(_calls.TryDequeue(out var call)
                ? new LlmResponse { ToolCalls = [call], StopReason = "tool_use" }
                : new LlmResponse { Text = "done", StopReason = "end_turn" });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, r.ToolCalls.FirstOrDefault(), true, null, r.StopReason);
        }
    }
}
