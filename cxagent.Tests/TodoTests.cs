using System.Text.Json;
using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The model's own plan. What matters is that it survives a long session — a plan deleted by
/// compaction is worse than no plan, because the model believes it still has one.
/// </summary>
public class TodoTests
{
    private static ToolCall Call(object args, string name = "todowrite") =>
        new() { Name = name, Id = "call-1", Arguments = JsonSerializer.SerializeToElement(args) };

    private static TodoList Written(object args)
    {
        var list = new TodoList();
        new TodoTool(list).TryInvoke(Call(args));
        return list;
    }

    [Fact]
    public void Write_RecordsTheItemsAndTheirStatuses()
    {
        var list = Written(new
        {
            todos = new object[]
            {
                new { text = "Read the parser", status = "completed" },
                new { text = "Fix the guard", status = "in_progress" },
                new { text = "Run the tests" },
            },
        });

        Assert.Equal(3, list.Items.Count);
        Assert.Equal(TodoStatus.Completed, list.Items[0].Status);
        Assert.Equal(TodoStatus.InProgress, list.Items[1].Status);
        Assert.Equal(TodoStatus.Pending, list.Items[2].Status);   // absent status defaults
        Assert.Equal(2, list.OpenCount);
    }

    /// <summary>
    /// Replaced whole, never patched. A partial update needs stable ids, and a model that
    /// mis-numbers one silently marks the wrong item done.
    /// </summary>
    [Fact]
    public void Write_ReplacesTheWholeList()
    {
        var list = new TodoList();
        var tool = new TodoTool(list);

        tool.TryInvoke(Call(new { todos = new[] { new { text = "first" }, new { text = "second" } } }));
        tool.TryInvoke(Call(new { todos = new[] { new { text = "only this now" } } }));

        Assert.Equal("only this now", Assert.Single(list.Items).Text);
    }

    /// <summary>Finishing the work and saying so is legitimate; a stale plan in the prompt is not.</summary>
    [Fact]
    public void Write_WithAnEmptyList_ClearsIt()
    {
        var list = new TodoList();
        var tool = new TodoTool(list);

        tool.TryInvoke(Call(new { todos = new[] { new { text = "something" } } }));
        var result = tool.TryInvoke(Call(new { todos = Array.Empty<object>() }));

        Assert.True(list.IsEmpty);
        Assert.Contains("cleared", result!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tolerant on the way in. The alternative to tolerance is losing the plan over a spelling —
    /// the model's INTENT was to record the item.
    /// </summary>
    [Theory]
    [InlineData("done", TodoStatus.Completed)]
    [InlineData("COMPLETE", TodoStatus.Completed)]
    [InlineData("in-progress", TodoStatus.InProgress)]
    [InlineData("doing", TodoStatus.InProgress)]
    [InlineData("skipped", TodoStatus.Cancelled)]
    [InlineData("nonsense", TodoStatus.Pending)]
    public void Write_AcceptsTheStatusWordsAModelActuallySends(string status, TodoStatus expected)
    {
        var list = Written(new { todos = new[] { new { text = "x", status } } });

        Assert.Equal(expected, Assert.Single(list.Items).Status);
    }

    /// <summary>Models send ["do this"] often enough that refusing it would be pedantry.</summary>
    [Fact]
    public void Write_AcceptsBareStrings()
    {
        var list = Written(new { todos = new[] { "first thing", "second thing" } });

        Assert.Equal(2, list.Items.Count);
        Assert.Equal("first thing", list.Items[0].Text);
        Assert.All(list.Items, i => Assert.Equal(TodoStatus.Pending, i.Status));
    }

    /// <summary>And a bare array instead of {"todos": [...]} — nothing else could be meant.</summary>
    [Fact]
    public void Write_AcceptsABareArrayAsTheList()
    {
        var list = new TodoList();
        new TodoTool(list).TryInvoke(Call(new[] { new { text = "just this" } }));

        Assert.Equal("just this", Assert.Single(list.Items).Text);
    }

    [Fact]
    public void Write_WithNoTodosArgument_SaysWhatItNeeds()
    {
        var result = new TodoTool(new TodoList()).TryInvoke(Call(new { something = "else" }));

        Assert.Contains("todos", result!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryInvoke_ReturnsNull_ForSomeoneElsesTool()
    {
        Assert.Null(new TodoTool(new TodoList()).TryInvoke(Call(new { }, name: "read_file")));
    }

    /// <summary>
    /// The result is the whole list, not "ok" — it is what the model reads to confirm the write
    /// landed as it meant, and the one place a mis-parsed status becomes visible to the thing that
    /// can correct it.
    /// </summary>
    [Fact]
    public void Write_HandsBackTheWholeListSoAMisreadStatusIsVisible()
    {
        var result = new TodoTool(new TodoList()).TryInvoke(Call(new
        {
            todos = new object[]
            {
                new { text = "already done", status = "completed" },
                new { text = "still going", status = "in_progress" },
            },
        }));

        Assert.Contains("already done", result!, StringComparison.Ordinal);
        Assert.Contains("still going", result, StringComparison.Ordinal);
        Assert.Contains("1 of 2 done", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// GROUPED, because a flat list of twelve buries the one line that matters. What is being worked
    /// on leads, what is left follows, what is finished goes last where it can be checked without
    /// being in the way.
    /// </summary>
    [Fact]
    public void Describe_GroupsByStatus_WithWhatIsHappeningNowFirst()
    {
        var result = new TodoTool(new TodoList()).TryInvoke(Call(new
        {
            todos = new object[]
            {
                new { text = "the finished one", status = "completed" },
                new { text = "the current one", status = "in_progress" },
                new { text = "the next one", status = "pending" },
                new { text = "the dropped one", status = "cancelled" },
            },
        }))!;

        Assert.Contains("1 of 4 done, 1 dropped", result, StringComparison.Ordinal);
        Assert.True(result.IndexOf("Now", StringComparison.Ordinal)
                  < result.IndexOf("Next", StringComparison.Ordinal));
        Assert.True(result.IndexOf("Next", StringComparison.Ordinal)
                  < result.IndexOf("Done", StringComparison.Ordinal));
    }

    /// <summary>
    /// FINISHED ITEMS ARE STRUCK THROUGH in the row. The transcript renders this body as markdown,
    /// so `~~` becomes real strikethrough — a settled item that LOOKS settled is readable at a
    /// glance, where a heading alone makes the reader hold "which group am I in" while scanning.
    /// </summary>
    [Fact]
    public void Describe_StrikesThroughWhatIsSettled_AndLeavesTheRestPlain()
    {
        var result = new TodoTool(new TodoList()).TryInvoke(Call(new
        {
            todos = new object[]
            {
                new { text = "the finished one", status = "completed" },
                new { text = "the dropped one", status = "cancelled" },
                new { text = "the current one", status = "in_progress" },
                new { text = "the next one", status = "pending" },
            },
        }))!;

        Assert.Contains("~~the finished one~~", result, StringComparison.Ordinal);
        Assert.Contains("~~the dropped one~~", result, StringComparison.Ordinal);

        // Still to do, so still plain — striking these would say the opposite of what is true.
        Assert.DoesNotContain("~~the current one~~", result, StringComparison.Ordinal);
        Assert.DoesNotContain("~~the next one~~", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE PROMPT FORM STAYS CLEAN. The row is rendered for a person; the prompt is read by the
    /// model, for which `~~` is punctuation to parse rather than a visual cue — and the `[x]` marker
    /// already says the item is done.
    /// </summary>
    [Fact]
    public void Render_DoesNotCarryTheRowsStrikethrough()
    {
        var list = Written(new { todos = new[] { new { text = "the finished one", status = "completed" } } });

        Assert.DoesNotContain("~~", list.Render(), StringComparison.Ordinal);
        Assert.Contains("- [x] the finished one", list.Render(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The PROMPT form stays flat and in the model's own order — it is the model's plan as it wrote
    /// it, and regrouping would quietly reorder the thing it is reading back.
    /// </summary>
    [Fact]
    public void Render_KeepsTheModelsOwnOrder()
    {
        var list = Written(new
        {
            todos = new object[]
            {
                new { text = "zebra first", status = "completed" },
                new { text = "alpha second" },
            },
        });

        var rendered = list.Render();

        Assert.True(rendered.IndexOf("zebra first", StringComparison.Ordinal)
                  < rendered.IndexOf("alpha second", StringComparison.Ordinal));
    }

    /// <summary>
    /// Completed items stay visible. The obvious economy is to render only what is open, and it is
    /// wrong: a model that cannot see what it has done will do it again.
    /// </summary>
    [Fact]
    public void Render_ShowsCompletedItemsToo()
    {
        var list = Written(new
        {
            todos = new object[]
            {
                new { text = "the finished one", status = "completed" },
                new { text = "the next one" },
            },
        });

        var rendered = list.Render();

        Assert.Contains("- [x] the finished one", rendered, StringComparison.Ordinal);
        Assert.Contains("- [ ] the next one", rendered, StringComparison.Ordinal);
    }

    /// <summary>An empty list must cost nothing — that is the common case.</summary>
    [Fact]
    public void Render_WhenEmpty_IsEmpty()
    {
        Assert.Equal("", new TodoList().Render());
    }

    /// <summary>
    /// THE PROPERTY THE WHOLE DESIGN EXISTS FOR. Held as a tool result the plan would be an ordinary
    /// message, and compaction removes the older half — deleting the model's plan exactly when the
    /// conversation got long enough to need one. In the system message it survives.
    /// </summary>
    [Fact]
    public async Task ThePlan_SurvivesCompaction()
    {
        var provider = new MockLlmProvider();

        // Turn one: the model writes a plan.
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [Call(new { todos = new[] { new { text = "the surviving step" } } })],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "noted", StopReason = "end_turn" });

        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 10);

        await agent.SendAsync("plan it", CancellationToken.None);
        Assert.Equal("the surviving step", Assert.Single(agent.Todos).Text);

        // Now compact, as a long session would.
        var summariser = new RecordingProvider(new LlmResponse
        {
            Text = "Earlier: the user asked for a plan.",
            Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5 },
        });
        await SessionCompressor.CompressAsync(agent.Context, summariser, CancellationToken.None);

        // The plan is still the agent's, and still reaches the model.
        Assert.Equal("the surviving step", Assert.Single(agent.Todos).Text);

        provider.EnqueueResponse(new LlmResponse { Text = "still here", StopReason = "end_turn" });
        await agent.SendAsync("what were you doing?", CancellationToken.None);

        var system = agent.Context.Messages.First(m => m.Role == "system").Content;
        Assert.Contains("the surviving step", system, StringComparison.Ordinal);
    }

    /// <summary>The tool is offered whether or not the list has anything in it.</summary>
    [Fact]
    public async Task TheTool_IsAlwaysOffered()
    {
        var provider = new ToolCapturingProvider();
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2);

        await agent.SendAsync("do something", CancellationToken.None);

        Assert.Contains("todowrite", provider.LastTools.Select(t => t.Name));
    }

    private sealed class ToolCapturingProvider : ILlmProvider
    {
        public List<ToolDefinition> LastTools { get; private set; } = [];
        public string ProviderId => "capturing";
        public string DisplayName => "Capturing";
        public string ModelId => "test-model";
        public ILlmProvider WithModel(string model) => this;
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            LastTools = tools ?? [];
            return Task.FromResult(new LlmResponse { Text = "done", StopReason = "end_turn" });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true, null, r.StopReason);
        }
    }

    /// <summary>
    /// THE OLD NAME STILL WORKS — a rename is invisible to a model working from habit or resuming a
    /// conversation whose earlier turns used it, and an unknown tool costs a turn to recover from.
    /// </summary>
    [Fact]
    public void Todos_StillAnswerToTheOldName()
    {
        var list = new TodoList();
        var tool = new TodoTool(list);

        var result = tool.TryInvoke(new ToolCall
        {
            Id = "c",
            Name = "update_todos",
            Arguments = System.Text.Json.JsonSerializer.SerializeToElement(
                new { todos = new[] { new { content = "ship it", status = "pending" } } }),
        });

        Assert.NotNull(result);
        Assert.Single(list.Items);
    }

    /// <summary>...and only the new name is advertised.</summary>
    [Fact]
    public void OnlyTheCurrentTodoNameIsOffered() =>
        Assert.Equal("todowrite", new TodoTool(new TodoList()).Definition.Name);
}
