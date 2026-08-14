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
    private static ToolCall Call(object args, string name = "question") =>
        new() { Name = name, Id = "call-1", Arguments = JsonSerializer.SerializeToElement(args) };

    private static AskUserTool Answering(params string[] answers) =>
        new((_, _) => Task.FromResult(new QuestionAnswers(answers)));

    /// <summary>Captures what the UI was asked to present.</summary>
    private static AskUserTool Capturing(
        out Func<IReadOnlyList<UserQuestion>> seen, params string[] answers)
    {
        IReadOnlyList<UserQuestion> captured = [];
        seen = () => captured;

        return new((qs, _) =>
        {
            captured = qs;
            return Task.FromResult(new QuestionAnswers(
                answers.Length > 0 ? answers : qs.Select(_ => "ok").ToArray()));
        });
    }

    [Fact]
    public async Task Ask_ReturnsWhatTheUserSaid()
    {
        var result = await Answering("the second one").TryInvokeAsync(
            Call(new { questions = new[] { new { question = "Which parser?" } } }),
            CancellationToken.None);

        Assert.Contains("the second one", result!);
        Assert.Contains("Which parser?", result!);
    }

    [Fact]
    public async Task Ask_PassesTheQuestionAndItsOptionsThrough()
    {
        var tool = Capturing(out var seen);

        await tool.TryInvokeAsync(
            Call(new
            {
                questions = new[]
                {
                    new
                    {
                        question = "Which one?",
                        header = "Parser",
                        options = new[]
                        {
                            new { label = "first", description = "the existing one" },
                            new { label = "second", description = "a rewrite" },
                        },
                    },
                },
            }),
            CancellationToken.None);

        var q = Assert.Single(seen());
        Assert.Equal("Which one?", q.Question);
        Assert.Equal("Parser", q.Header);
        Assert.Equal(["first", "second"], q.Choices.Select(c => c.Label));

        // THE DESCRIPTION IS THE POINT. Two bare labels ask the user to guess what the model meant.
        Assert.Equal(["the existing one", "a rewrite"], q.Choices.Select(c => c.Description));
    }

    /// <summary>
    /// SKIPPING IS AN ANSWER, and the model is told what it means. An empty result that just came
    /// back as "" would read as a user who said nothing rather than one who declined to choose.
    /// </summary>
    [Fact]
    public async Task Ask_WhenTheUserSkips_TellsTheModelToUseItsOwnJudgement()
    {
        var tool = new AskUserTool((_, _) => Task.FromResult(QuestionAnswers.Cancel));

        var result = await tool.TryInvokeAsync(
            Call(new { questions = new[] { new { question = "Which one?" } } }),
            CancellationToken.None);

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
        var tool = new AskUserTool((_, ct) => Task.FromCanceled<QuestionAnswers>(ct));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await tool.TryInvokeAsync(
            Call(new { questions = new[] { new { question = "Which one?" } } }), cts.Token);

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
        var tool = Capturing(out var seen);

        await tool.TryInvokeAsync(
            new ToolCall
            {
                Name = "question", Id = "c",
                Arguments = JsonSerializer.SerializeToElement("Which one?"),
            },
            CancellationToken.None);

        Assert.Equal("Which one?", Assert.Single(seen()).Question);
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

        var child = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2,
            isSubAgent: true,
            askUser: (_, _) => Task.FromResult(new QuestionAnswers(["never reachable"])));

        await child.SendAsync("do something", CancellationToken.None);

        Assert.DoesNotContain("question", provider.LastTools.Select(t => t.Name));
    }

    /// <summary>And the session's own agent IS offered it — the guard must not withhold from everyone.</summary>
    [Fact]
    public async Task TheSessionAgent_IsOfferedTheTool()
    {
        var provider = new ToolCapturingProvider();

        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2,
            askUser: (_, _) => Task.FromResult(new QuestionAnswers(["ok"])));

        await agent.SendAsync("do something", CancellationToken.None);

        Assert.Contains("question", provider.LastTools.Select(t => t.Name));
    }

    /// <summary>A host with no UI offers nothing — there is nobody to wait for.</summary>
    [Fact]
    public async Task WithNoWayToAsk_TheToolIsAbsent()
    {
        var provider = new ToolCapturingProvider();

        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2);

        await agent.SendAsync("do something", CancellationToken.None);

        Assert.DoesNotContain("question", provider.LastTools.Select(t => t.Name));
    }

    private sealed class ToolCapturingProvider : ILlmProvider
    {
        public List<ToolDefinition> LastTools { get; private set; } = [];
        public string ProviderId => "capturing";
        public string DisplayName => "Capturing";
        public string ModelId => "test-model";
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
        var prompt = new CxAgent.UI.QuestionPromptControl([new UserQuestion("Which config file?")]);
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
        Assert.Equal("config-prod.yaml", Assert.Single(prompt.Completion.Result.Answers));
    }

    /// <summary>
    /// An accidental Enter is not a decision. It would otherwise read as a skip — Skip() also
    /// completes with "" — and silently tell the model to use its own judgement.
    /// </summary>
    [Fact]
    public void EmptyEnter_LeavesTheQuestionUp()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl([new UserQuestion("Which config file?")]);
        var input = FindPrompt(prompt.BuildContent());

        input.ProcessKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        Assert.False(prompt.Completion.IsCompleted);
    }

    /// <summary>Escape resolves with "", which the tool reads as "proceed on your own judgement".</summary>
    [Fact]
    public void Skip_CompletesWithNothing()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl([new UserQuestion("Which config file?")]);

        prompt.Skip();

        Assert.True(prompt.Completion.IsCompleted);
        Assert.True(prompt.Completion.Result.Cancelled);
    }

    // --- stepping through several questions ---

    private static void Type(SharpConsoleUI.Controls.PromptControl input, string text)
    {
        foreach (var c in text)
            input.ProcessKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));

        input.ProcessKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
    }

    /// <summary>
    /// ONE ON SCREEN AT A TIME, answers returned together. The composer is a few rows tall: three
    /// questions with described option lists stacked into it would clip the last of them.
    /// </summary>
    [Fact]
    public void SeveralQuestions_AreAnsweredOneStepAtATime()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which config?"),
            new UserQuestion("Which branch?"),
        ]);

        SharpConsoleUI.Controls.IWindowControl current = prompt.BuildContent();
        prompt.StepChanged += next => current = next;

        Type(FindPrompt(current), "config-prod.yaml");
        Assert.False(prompt.Completion.IsCompleted);   // the first answer is not the whole run

        Type(FindPrompt(current), "master");
        Assert.False(prompt.Completion.IsCompleted);   // ...nor is the last: the summary comes first

        Type(FindPrompt(current), "");                 // Enter on the summary sends

        Assert.True(prompt.Completion.IsCompleted);
        Assert.Equal(["config-prod.yaml", "master"], prompt.Completion.Result.Answers);
    }

    /// <summary>
    /// BACK, so an answer can be reconsidered. Someone who realises their first choice was wrong
    /// while reading the second question can go and change it — which a single submit-everything
    /// panel cannot offer.
    /// </summary>
    [Fact]
    public void Back_ReturnsToThePreviousQuestion()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which config?"),
            new UserQuestion("Which branch?"),
        ]);

        SharpConsoleUI.Controls.IWindowControl current = prompt.BuildContent();
        prompt.StepChanged += next => current = next;

        Type(FindPrompt(current), "wrong.yaml");
        Assert.True(prompt.Back());

        Type(FindPrompt(current), "config-prod.yaml");   // answered again, this time correctly
        Type(FindPrompt(current), "master");
        Type(FindPrompt(current), "");                   // send from the summary

        Assert.Equal(["config-prod.yaml", "master"], prompt.Completion.Result.Answers);
    }

    [Fact]
    public void Back_OnTheFirstQuestion_DoesNothing()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl([new UserQuestion("Which config?")]);
        prompt.BuildContent();

        Assert.False(prompt.Back());
    }

    /// <summary>
    /// ESCAPE MID-RUN KEEPS WHAT WAS ALREADY ANSWERED. Those were real decisions, and making the
    /// user repeat them punishes them for changing their mind about the third.
    /// </summary>
    [Fact]
    public void SkippingPartWayThrough_KeepsTheAnswersAlreadyGiven()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which config?"),
            new UserQuestion("Which branch?"),
        ]);

        SharpConsoleUI.Controls.IWindowControl current = prompt.BuildContent();
        prompt.StepChanged += next => current = next;

        Type(FindPrompt(current), "config-prod.yaml");
        prompt.Skip();

        var result = prompt.Completion.Result;
        Assert.False(result.Cancelled);
        Assert.Equal("config-prod.yaml", result.Answers[0]);
        Assert.Equal("", result.Answers[1]);            // skipped: "you decide"
    }

    /// <summary>Escaping before answering anything is a CANCEL — a different message to the model
    /// than a set of blank answers.</summary>
    [Fact]
    public void SkippingImmediately_IsACancel()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which config?"),
            new UserQuestion("Which branch?"),
        ]);
        prompt.BuildContent();

        prompt.Skip();

        Assert.True(prompt.Completion.Result.Cancelled);
    }

    // --- the summary, before anything is sent ---

    /// <summary>
    /// THE LAST CHANCE TO CHANGE A DECISION, and the only place the set is visible as a set.
    /// Stepping is what makes several questions readable, and it also means that by question three
    /// nobody remembers exactly what they said to question one.
    /// </summary>
    [Fact]
    public void SeveralQuestions_AreReviewedBeforeTheyAreSent()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which config?", "Config file"),
            new UserQuestion("Which branch?", "Branch"),
        ]);

        SharpConsoleUI.Controls.IWindowControl current = prompt.BuildContent();
        prompt.StepChanged += next => current = next;

        Type(FindPrompt(current), "config-prod.yaml");
        Type(FindPrompt(current), "master");

        // Not sent yet — the summary is on screen, showing both answers under their headers.
        Assert.False(prompt.Completion.IsCompleted);
        var text = Rendered(current);
        Assert.Contains("Your answers", text);
        Assert.Contains("Config file", text);
        Assert.Contains("config-prod.yaml", text);
        Assert.Contains("master", text);
    }

    /// <summary>Back from the summary reopens the last question — the one just read, and so the
    /// one most likely to want changing.</summary>
    [Fact]
    public void BackFromTheSummary_ReopensTheLastQuestion()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which config?"),
            new UserQuestion("Which branch?"),
        ]);

        SharpConsoleUI.Controls.IWindowControl current = prompt.BuildContent();
        prompt.StepChanged += next => current = next;

        Type(FindPrompt(current), "config-prod.yaml");
        Type(FindPrompt(current), "wrong-branch");

        Assert.True(prompt.Back());
        Type(FindPrompt(current), "master");     // answered again
        Type(FindPrompt(current), "");           // send

        Assert.Equal(["config-prod.yaml", "master"], prompt.Completion.Result.Answers);
    }

    /// <summary>One question needs no review: the user is looking at the answer they just gave.</summary>
    [Fact]
    public void ASingleQuestion_IsSentWithoutASummary()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl([new UserQuestion("Which config?")]);
        var content = prompt.BuildContent();

        Type(FindPrompt(content), "config-prod.yaml");

        Assert.True(prompt.Completion.IsCompleted);
    }

    /// <summary>Escape on the summary sends what is there — the answers were given, and discarding
    /// them because someone would rather not confirm is a punishment for reading.</summary>
    [Fact]
    public void EscapeOnTheSummary_SendsTheAnswers()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which config?"),
            new UserQuestion("Which branch?"),
        ]);

        SharpConsoleUI.Controls.IWindowControl current = prompt.BuildContent();
        prompt.StepChanged += next => current = next;

        Type(FindPrompt(current), "config-prod.yaml");
        Type(FindPrompt(current), "master");
        prompt.Skip();

        var result = prompt.Completion.Result;
        Assert.False(result.Cancelled);
        Assert.Equal(["config-prod.yaml", "master"], result.Answers);
    }

    /// <summary>
    /// FOCUS LANDS ON THE LIST when there is one. The drive found this: focus started on the panel,
    /// so the first Enter did nothing and the user had to press Down before the list would answer —
    /// which reads as a hung app.
    /// </summary>
    [Fact]
    public void AQuestionWithOptions_FocusesTheList()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which config?", Options: [new QuestionOption("a"), new QuestionOption("b")]),
        ]);
        prompt.BuildContent();

        Assert.IsType<SharpConsoleUI.Controls.ListControl>(prompt.FocusTarget);
    }

    /// <summary>
    /// AND THE FIRST OPTION IS ALREADY HIGHLIGHTED. A list opens with SelectedIndex = -1, so Enter
    /// had nothing to activate — the drive showed a user pressing it twice with no effect. It is
    /// also what makes "put your recommendation first" mean anything.
    /// </summary>
    [Fact]
    public void AQuestionWithOptions_StartsOnTheFirstOption()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which config?",
                Options: [new QuestionOption("prod (Recommended)"), new QuestionOption("dev")]),
        ]);
        var content = prompt.BuildContent();

        var list = Assert.IsType<SharpConsoleUI.Controls.ListControl>(prompt.FocusTarget);
        Assert.Equal(0, list.SelectedIndex);

        // ...so submitting with nothing typed answers with it.
        FindPrompt(content).ProcessKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        Assert.Equal("prod (Recommended)", Assert.Single(prompt.Completion.Result.Answers));
    }

    [Fact]
    public void AFreeTextQuestion_FocusesTheField()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl([new UserQuestion("Which config?")]);
        prompt.BuildContent();

        Assert.IsType<SharpConsoleUI.Controls.PromptControl>(prompt.FocusTarget);
    }

    /// <summary>Everything the panel would paint, for asserting on what is shown.</summary>
    private static string Rendered(SharpConsoleUI.Controls.IWindowControl content)
    {
        var panel = Assert.IsType<SharpConsoleUI.Controls.ScrollablePanelControl>(content);

        return string.Join("\n", panel.GetChildren()
            .OfType<SharpConsoleUI.Controls.MarkupControl>()
            .Select(m => m.Text));
    }

    // --- choosing more than one ---

    /// <summary>
    /// SPACE CHECKS, ENTER SUBMITS, and the answer is every label that was checked. Some questions
    /// are genuinely not exclusive — which checks to run, which files to include — and forcing one
    /// answer makes the model ask the same question three times.
    /// </summary>
    [Fact]
    public void MultiSelect_AnswersWithEveryCheckedOption()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which checks?", Multiple: true,
                Options: [new QuestionOption("lint"), new QuestionOption("tests"),
                          new QuestionOption("typecheck")]),
        ]);
        var content = prompt.BuildContent();

        var list = Assert.IsType<SharpConsoleUI.Controls.ListControl>(prompt.FocusTarget);

        Check(list, 0);
        Check(list, 2);
        prompt.SubmitFromList();

        Assert.Equal("lint, typecheck", Assert.Single(prompt.Completion.Result.Answers));
    }

    /// <summary>
    /// NOTHING CHECKED IS NOT AN ANSWER. Advancing on an empty selection would record "none of
    /// these" as a decision the user never made.
    /// </summary>
    [Fact]
    public void MultiSelect_WithNothingChecked_DoesNotAdvance()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which checks?", Multiple: true,
                Options: [new QuestionOption("lint"), new QuestionOption("tests")]),
        ]);
        var content = prompt.BuildContent();

        prompt.SubmitFromList();

        Assert.False(prompt.Completion.IsCompleted);
    }

    /// <summary>
    /// NOTHING IS PRE-CHECKED, unlike single-select where the first option is pre-highlighted.
    /// Checking one for the user would put a choice in the answer they never made — and on a
    /// question that accepts several, the empty set is a meaningful starting point.
    /// </summary>
    [Fact]
    public void MultiSelect_StartsWithNothingChosen()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which checks?", Multiple: true,
                Options: [new QuestionOption("lint"), new QuestionOption("tests")]),
        ]);
        prompt.BuildContent();

        var list = Assert.IsType<SharpConsoleUI.Controls.ListControl>(prompt.FocusTarget);
        Assert.Empty(list.GetCheckedItems());
    }

    /// <summary>Typing still overrides the list — an answer the model did not think of.</summary>
    [Fact]
    public void MultiSelect_StillAcceptsATypedAnswer()
    {
        var prompt = new CxAgent.UI.QuestionPromptControl(
        [
            new UserQuestion("Which checks?", Multiple: true,
                Options: [new QuestionOption("lint"), new QuestionOption("tests")]),
        ]);
        var content = prompt.BuildContent();

        Type(FindPrompt(content), "only the slow ones");

        Assert.Equal("only the slow ones", Assert.Single(prompt.Completion.Result.Answers));
    }

    /// <summary>
    /// Checks an item, which is what Space does to it.
    ///
    /// <para>Set directly rather than via ProcessKey: the list refuses every key unless it HasFocus,
    /// and that is computed from a live window's focus manager.</para>
    /// </summary>
    private static void Check(SharpConsoleUI.Controls.ListControl list, int index) =>
        list.Items[index].IsChecked = true;

    /// <summary>The free-text prompt inside the question panel.</summary>
    private static SharpConsoleUI.Controls.PromptControl FindPrompt(
        SharpConsoleUI.Controls.IWindowControl content)
    {
        var panel = Assert.IsType<SharpConsoleUI.Controls.ScrollablePanelControl>(content);

        return panel.GetChildren().OfType<SharpConsoleUI.Controls.PromptControl>().Single();
    }

    // --- several questions in one call ---

    /// <summary>
    /// RELATED DECISIONS COST ONE INTERRUPTION, not three. A model gathering three answers by
    /// calling three times stops the user three separate times, and each call is a round trip.
    /// </summary>
    [Fact]
    public async Task Ask_CarriesSeveralQuestionsInOneCall()
    {
        var tool = Capturing(out var seen, "config-prod.yaml", "yes", "rewrite");

        var result = await tool.TryInvokeAsync(
            Call(new
            {
                questions = new[]
                {
                    new { question = "Which config?" },
                    new { question = "Run the tests?" },
                    new { question = "Parser approach?" },
                },
            }),
            CancellationToken.None);

        Assert.Equal(3, seen().Count);

        // PAIRED WITH THEIR QUESTIONS. Three loose answers leave the model matching by position,
        // and one that miscounts acts on the wrong decision believing the user chose it.
        Assert.Contains("\"Which config?\" = \"config-prod.yaml\"", result!);
        Assert.Contains("\"Run the tests?\" = \"yes\"", result!);
        Assert.Contains("\"Parser approach?\" = \"rewrite\"", result!);
    }

    /// <summary>
    /// Past the cap the extras are DROPPED AND REPORTED. A model that believes it asked five
    /// questions and hears back about four will act on an answer nobody gave.
    /// </summary>
    [Fact]
    public async Task Ask_BeyondTheCap_SaysWhatItDidNotAsk()
    {
        var tool = Capturing(out var seen);

        var result = await tool.TryInvokeAsync(
            Call(new
            {
                questions = Enumerable.Range(1, AskUserTool.MaxQuestions + 2)
                    .Select(i => new { question = $"Question {i}?" })
                    .ToArray(),
            }),
            CancellationToken.None);

        Assert.Equal(AskUserTool.MaxQuestions, seen().Count);
        Assert.Contains("not asked", result!);
        Assert.Contains("2 further questions", result!);
    }

    /// <summary>A skipped question is reported as one — it means "you decide", not "no answer".</summary>
    [Fact]
    public async Task Ask_ASkippedQuestion_IsReportedAsSkipped()
    {
        var tool = Answering("config-prod.yaml", "");

        var result = await tool.TryInvokeAsync(
            Call(new
            {
                questions = new[]
                {
                    new { question = "Which config?" },
                    new { question = "Run the tests?" },
                },
            }),
            CancellationToken.None);

        Assert.Contains("(skipped)", result!);
        Assert.Contains("your own judgement", result!);
    }

    [Fact]
    public async Task Ask_MultipleChoiceIsCarriedThrough()
    {
        var tool = Capturing(out var seen);

        await tool.TryInvokeAsync(
            Call(new
            {
                questions = new[]
                {
                    new
                    {
                        question = "Which checks?",
                        multiple = true,
                        options = new[] { new { label = "lint" }, new { label = "tests" } },
                    },
                },
            }),
            CancellationToken.None);

        Assert.True(Assert.Single(seen()).Multiple);
    }

    // --- shapes a model might send ---

    /// <summary>
    /// THE OLDER SINGLE-QUESTION SHAPE STILL WORKS. It was this tool's whole schema until recently,
    /// and a model producing it should reach the user rather than receive a schema lecture.
    /// </summary>
    [Fact]
    public async Task Ask_AcceptsASingleQuestionAtTheTopLevel()
    {
        var tool = Capturing(out var seen);

        await tool.TryInvokeAsync(
            Call(new { question = "Which one?", options = new[] { "a", "b" } }),
            CancellationToken.None);

        var q = Assert.Single(seen());
        Assert.Equal("Which one?", q.Question);

        // Plain strings are labels, for the same reason.
        Assert.Equal(["a", "b"], q.Choices.Select(c => c.Label));
    }

    /// <summary>Options past the per-question cap are dropped: more than a handful is a menu.</summary>
    [Fact]
    public async Task Ask_CapsTheNumberOfOptions()
    {
        var tool = Capturing(out var seen);

        await tool.TryInvokeAsync(
            Call(new
            {
                questions = new[]
                {
                    new
                    {
                        question = "Which?",
                        options = Enumerable.Range(1, AskUserTool.MaxOptions + 3)
                            .Select(i => new { label = $"option {i}" }).ToArray(),
                    },
                },
            }),
            CancellationToken.None);

        Assert.Equal(AskUserTool.MaxOptions, Assert.Single(seen()).Choices.Count);
    }

    /// <summary>
    /// THE OLD NAME STILL WORKS. A rename is invisible to a model working from habit, or to a
    /// RESUMED conversation whose earlier turns called it ask_user — and an unknown tool is a hard
    /// failure that costs a turn to recover from, for a call that is completely unambiguous.
    /// </summary>
    [Fact]
    public async Task Ask_StillAnswersToItsOldName()
    {
        var tool = Capturing(out var seen);

        var result = await tool.TryInvokeAsync(
            new ToolCall
            {
                Name = "ask_user", Id = "c",
                Arguments = JsonSerializer.SerializeToElement(
                    new { questions = new[] { new { question = "Which one?" } } }),
            },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Which one?", Assert.Single(seen()).Question);
    }

    /// <summary>...but only the new name is advertised, so nothing pulls the model backwards.</summary>
    [Fact]
    public void OnlyTheCurrentNameIsOffered()
    {
        Assert.Equal("question", Answering("x").Definition.Name);
    }

    /// <summary>And a call for some other tool is still not ours.</summary>
    [Fact]
    public async Task ACallForAnotherTool_IsDeclined()
    {
        Assert.Null(await Answering("x").TryInvokeAsync(
            Call(new { questions = new[] { new { question = "?" } } }, name: "read_file"),
            CancellationToken.None));
    }

    // --- the description, which is what decides whether the tool is ever called ---

    /// <summary>
    /// IT DESCRIBES WHAT THE TOOL IS FOR. The previous version spent a paragraph arguing against
    /// itself — "use this ONLY when you cannot proceed", "a question costs them more than a tool
    /// call costs you" — and across three live drives the model never called it once. On the last,
    /// it wanted to consult the user and asked in PROSE, which is the failure the tool exists to
    /// prevent, caused by the tool's own description.
    /// </summary>
    [Fact]
    public void TheDescriptionSaysWhatTheToolIsFor()
    {
        var description = Answering("x").Definition.Description;

        Assert.Contains("preferences", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ambiguous", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ONLY when you cannot proceed", description);
        Assert.DoesNotContain("costs you", description);
    }

    /// <summary>One line of restraint stays: do not ask what reading the code would answer.</summary>
    [Fact]
    public void TheDescriptionStillSaysToLookFirst()
    {
        Assert.Contains("read the code", Answering("x").Definition.Description);
    }
}
