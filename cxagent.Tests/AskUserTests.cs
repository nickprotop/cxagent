using System.Text.Json;
using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The model asking the user a question. The interesting properties are what happens when there is
/// nobody to ask, and what happens when the user does not want to answer.
/// </summary>
public class AskUserTests
{
    private static ToolCall Call(object args, string name = "ask_user") =>
        new() { Name = name, Id = "call-1", Arguments = JsonSerializer.SerializeToElement(args) };

    private static AskUserTool Answering(string answer) =>
        new((_, _, _) => Task.FromResult(answer));

    [Fact]
    public async Task Ask_ReturnsWhatTheUserSaid()
    {
        var result = await Answering("the second one").TryInvokeAsync(
            Call(new { question = "Which parser?" }), CancellationToken.None);

        Assert.Equal("the second one", result);
    }

    [Fact]
    public async Task Ask_PassesTheQuestionAndOptionsThrough()
    {
        string? asked = null;
        IReadOnlyList<string> offered = [];

        var tool = new AskUserTool((q, o, _) =>
        {
            asked = q;
            offered = o;
            return Task.FromResult("ok");
        });

        await tool.TryInvokeAsync(
            Call(new { question = "Which one?", options = new[] { "first", "second" } }),
            CancellationToken.None);

        Assert.Equal("Which one?", asked);
        Assert.Equal(["first", "second"], offered);
    }

    /// <summary>
    /// SKIPPING IS AN ANSWER, and the model is told what it means. An empty result that just came
    /// back as "" would read as a user who said nothing rather than one who declined to choose.
    /// </summary>
    [Fact]
    public async Task Ask_WhenTheUserSkips_TellsTheModelToUseItsOwnJudgement()
    {
        var result = await Answering("").TryInvokeAsync(
            Call(new { question = "Which one?" }), CancellationToken.None);

        Assert.Contains("dismissed", result!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("own judgement", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CANCELLATION STILL RETURNS A RESULT. An unanswered tool call is the orphan that 400s a
    /// session permanently, and losing a session to a cancelled question would be a bitter way to
    /// find that out.
    /// </summary>
    [Fact]
    public async Task Ask_WhenCancelled_StillReturnsSomething()
    {
        var tool = new AskUserTool((_, _, ct) => Task.FromCanceled<string>(ct));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await tool.TryInvokeAsync(Call(new { question = "Which one?" }), cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("cancelled", result!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ask_WithNoQuestion_SaysWhatItNeeds()
    {
        var result = await Answering("x").TryInvokeAsync(Call(new { options = new[] { "a" } }),
            CancellationToken.None);

        Assert.Contains("question", result!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ask_AcceptsABareStringAsTheQuestion()
    {
        string? asked = null;
        var tool = new AskUserTool((q, _, _) => { asked = q; return Task.FromResult("ok"); });

        await tool.TryInvokeAsync(
            new ToolCall
            {
                Name = "ask_user", Id = "c",
                Arguments = JsonSerializer.SerializeToElement("Which one?"),
            },
            CancellationToken.None);

        Assert.Equal("Which one?", asked);
    }

    [Fact]
    public async Task TryInvoke_ReturnsNull_ForSomeoneElsesTool()
    {
        Assert.Null(await Answering("x").TryInvokeAsync(
            Call(new { question = "?" }, name: "read_file"), CancellationToken.None));
    }

    /// <summary>
    /// THE PROPERTY THAT MATTERS MOST. A child has no user: its output goes to its parent, and a
    /// child blocking on a question nobody can see is a hang that ends only when the parent's turn
    /// is cancelled. The tool is WITHHELD rather than refused — the same mechanism that makes "no
    /// sub-agents of sub-agents" structural rather than a rule.
    /// </summary>
    [Fact]
    public async Task ASubAgent_IsNeverOfferedTheTool_EvenWhenOneIsPassedIn()
    {
        var provider = new ToolCapturingProvider();

        var child = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2,
            isSubAgent: true,
            askUser: (_, _, _) => Task.FromResult("this must never be reachable"));

        await child.SendAsync("do something", CancellationToken.None);

        Assert.DoesNotContain("ask_user", provider.LastTools.Select(t => t.Name));
    }

    /// <summary>And the session's own agent IS offered it — the guard must not withhold from everyone.</summary>
    [Fact]
    public async Task TheSessionAgent_IsOfferedTheTool()
    {
        var provider = new ToolCapturingProvider();

        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2,
            askUser: (_, _, _) => Task.FromResult("ok"));

        await agent.SendAsync("do something", CancellationToken.None);

        Assert.Contains("ask_user", provider.LastTools.Select(t => t.Name));
    }

    /// <summary>A host with no UI offers nothing — there is nobody to wait for.</summary>
    [Fact]
    public async Task WithNoWayToAsk_TheToolIsAbsent()
    {
        var provider = new ToolCapturingProvider();

        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2);

        await agent.SendAsync("do something", CancellationToken.None);

        Assert.DoesNotContain("ask_user", provider.LastTools.Select(t => t.Name));
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

    // --- the prompt control, which is where the answer is actually collected ---

    /// <summary>
    /// THE ANSWER RESOLVES ON ENTER, NOT ON EVERY KEYSTROKE.
    ///
    /// <para>This was wired to InputChanged, which fires per character — so answering
    /// "config-prod.yaml" sent the model "c", restored the composer under the user mid-word, and
    /// spilled "onfig-prod.yaml" into the transcript as stray text. The model then reasoned about
    /// the single character it had been given. Every unit test passed throughout: they covered the
    /// tool, and the defect was in the control that feeds it.</para>
    /// </summary>
    [Fact]
    public void TypedAnswer_ResolvesOnlyWhenSubmitted()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl("Which config file?", []);
        var content = prompt.BuildContent();
        var input = FindPrompt(content);

        // Typing does not answer anything.
        // REAL KEYSTROKES, because the defect was in which event the answer hung off — a test that
        // set Input directly would have passed against the broken version too.
        foreach (var c in "config-prod.yaml")
            input.ProcessKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));

        Assert.False(prompt.Completion.IsCompleted, "typing a character must not submit the answer");

        // Submitting does, and with the WHOLE thing.
        input.ProcessKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        Assert.True(prompt.Completion.IsCompleted);
        Assert.Equal("config-prod.yaml", prompt.Completion.Result);
    }

    /// <summary>
    /// An accidental Enter is not a decision. It would otherwise read as a skip — Skip() also
    /// completes with "" — and silently tell the model to use its own judgement.
    /// </summary>
    [Fact]
    public void EmptyEnter_LeavesTheQuestionUp()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl("Which config file?", []);
        var input = FindPrompt(prompt.BuildContent());

        input.ProcessKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        Assert.False(prompt.Completion.IsCompleted);
    }

    /// <summary>Escape resolves with "", which the tool reads as "proceed on your own judgement".</summary>
    [Fact]
    public void Skip_CompletesWithNothing()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl("Which config file?", []);

        prompt.Skip();

        Assert.True(prompt.Completion.IsCompleted);
        Assert.Equal("", prompt.Completion.Result);
    }

    /// <summary>The free-text prompt inside the question panel.</summary>
    private static SharpConsoleUI.Controls.PromptControl FindPrompt(
        SharpConsoleUI.Controls.IWindowControl content)
    {
        var panel = Assert.IsType<SharpConsoleUI.Controls.ScrollablePanelControl>(content);

        return panel.GetChildren().OfType<SharpConsoleUI.Controls.PromptControl>().Single();
    }
}
