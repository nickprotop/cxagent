using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Text injected PER TURN — neither the system prompt nor a tool description, and the channel
/// nobody had looked at.
///
/// <para>Both of these name a tool in text the model reads every turn, and both keep saying it when
/// the tool is withheld. The list and the notice are still worth showing: what changes is the
/// sentence telling the model to use something it no longer has.</para>
/// </summary>
public class InjectedTextSelectionTests
{
    // --- The todo list header ----------------------------------------------------------

    [Fact]
    public void TheTodoHeaderNamesTodowriteWhenItIsOffered()
    {
        var list = new TodoList();
        list.Replace([new TodoItem("ship it", TodoStatus.Pending)]);

        Assert.Contains("todowrite", list.Render(toolOffered: true));
    }

    [Fact]
    public void TheTodoHeaderDoesNotNameTodowriteWhenItIsWithheld()
    {
        // THE LIST OUTLIVES THE TOOL. Items live in the agent's state, so a selection applied later
        // leaves a non-empty list rendered beside an instruction to update it with something the
        // model no longer has.
        var list = new TodoList();
        list.Replace([new TodoItem("ship it", TodoStatus.Pending)]);

        var rendered = list.Render(toolOffered: false);

        Assert.DoesNotContain("todowrite", rendered);

        // THE LIST STILL SHOWS. It is what the agent was doing, and hiding it would lose the record
        // as well as the remedy.
        Assert.Contains("ship it", rendered);
    }

    [Fact]
    public void AnEmptyListRendersNothingEitherWay()
    {
        Assert.Equal("", new TodoList().Render(toolOffered: true));
        Assert.Equal("", new TodoList().Render(toolOffered: false));
    }

    // --- The compaction notice ---------------------------------------------------------

    [Fact]
    public async Task TheCompactionNoticeNamesSkillWhenItIsOffered()
    {
        var notice = await NoticeFor(skillToolOffered: true);

        Assert.Contains("call skill again", notice);
    }

    [Fact]
    public async Task TheCompactionNoticeDoesNotNameSkillWhenItIsWithheld()
    {
        // THE LOSS IS ALWAYS WORTH SAYING; the remedy only when it exists. A line naming a withheld
        // tool sends the model to spend a turn discovering that.
        var notice = await NoticeFor(skillToolOffered: false);

        Assert.DoesNotContain("call skill again", notice);
        Assert.Contains("removed by compaction", notice);
    }

    /// <summary>Compresses a context holding a loaded skill, and returns the summary message.</summary>
    private static async Task<string> NoticeFor(bool skillToolOffered)
    {
        var context = new AgentContext();
        context.Messages.Add(new ChatMessage { Role = "system", Content = "prompt" });
        context.Messages.Add(new ChatMessage { Role = "user", Content = "do it" });
        // THE SKILL MUST LAND IN THE REMOVED RANGE. The head is pinned (PinnedHeadCount), so a
        // marker placed at the top survives compaction and the notice never fires — which is what
        // the first version of this fixture did, failing the "when offered" case too.
        for (var i = 0; i < 4; i++)
        {
            context.Messages.Add(new ChatMessage { Role = "user", Content = $"early {i}" });
            context.Messages.Add(new ChatMessage { Role = "assistant", Content = $"sure {i}" });
        }

        context.Messages.Add(new ChatMessage
        {
            Role = "tool",

            // ToolCallId IS REQUIRED: LoadedIn skips any message without one, so a marker on a bare
            // message is invisible to it. That is deliberate in the product — a marker quoted in
            // prose must not count as a loaded skill.
            ToolCallId = "c1",
            Content = CxAgent.Core.Skills.SkillLoader.BodyMarkerPrefix + "writing-tests]\nbody",
        });

        for (var i = 0; i < 12; i++)
        {
            context.Messages.Add(new ChatMessage { Role = "user", Content = $"more {i}" });
            context.Messages.Add(new ChatMessage { Role = "assistant", Content = $"ok {i}" });
        }

        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "a summary",
            StopReason = "end_turn",
            Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
        });

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None,
            meter: null, skillToolOffered: skillToolOffered);

        return string.Join("\n", context.Messages.Select(m => m.Content ?? ""));
    }
}
