using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The load tool. What matters here is not "does it return the body" but the three answers that are
/// easy to ship broken: an unknown name, a second call for the same skill, and the same call again
/// after compaction removed what the first one loaded.
/// </summary>
public class SkillLoaderTests
{
    private static SkillInfo Skill(string name, string body = "# Heading\n\nDo the thing.") =>
        new(name, $"Use when {name}.", $"/tmp/skills/{name}", body);

    private static SkillLoader Loader(params SkillInfo[] skills) =>
        new(() => new SkillCatalogResult(skills, [], skills.Length > 0 ? "/tmp/skills" : null));

    private static ToolCall Call(string? name, string tool = "skill") => new()
    {
        Name = tool,
        Id = "call-1",
        Arguments = JsonDocument.Parse(
            name is null ? "{}" : $"{{\"name\":\"{name}\"}}").RootElement.Clone(),
    };

    /// <summary>A tool result as the loop appends it: ToolCallId set, content from the tool.</summary>
    private static ChatMessage Result(string content, string id = "call-1") =>
        new() { Role = "tool", Content = content, ToolCallId = id };

    [Fact]
    public void TryInvoke_ReturnsTheBody_AndItsDirectory()
    {
        var result = Loader(Skill("rtl-aware-development")).TryInvoke(Call("rtl-aware-development"), []);

        Assert.NotNull(result);
        Assert.Contains("Do the thing.", result!, StringComparison.Ordinal);
        Assert.Contains("/tmp/skills/rtl-aware-development", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// The body announces which skill it is, so the "already loaded" scan — and compaction — can
    /// recognise it without joining back to the assistant message that made the call.
    /// </summary>
    [Fact]
    public void TryInvoke_MarksTheBodyWithTheSkillName()
    {
        var result = Loader(Skill("planner-notes")).TryInvoke(Call("planner-notes"), []);

        Assert.StartsWith("[skill: planner-notes]", result!, StringComparison.Ordinal);
    }

    /// <summary>A name this loader does not own is not its business — the chain tries MCP next.</summary>
    [Fact]
    public void TryInvoke_ReturnsNull_ForSomeoneElsesTool()
    {
        Assert.Null(Loader(Skill("anything")).TryInvoke(Call("anything", tool: "read_file"), []));
    }

    /// <summary>
    /// AN ERROR STRING, NOT AN EXCEPTION, and it names the valid skills. A model that guessed wrong
    /// has no other way to discover the right name from inside a turn.
    /// </summary>
    [Fact]
    public void TryInvoke_OnAnUnknownName_ErrorsAndListsTheRealOnes()
    {
        var result = Loader(Skill("alpha"), Skill("beta")).TryInvoke(Call("gamma"), []);

        Assert.NotNull(result);
        Assert.Contains("No skill named 'gamma'", result!, StringComparison.Ordinal);
        Assert.Contains("alpha", result, StringComparison.Ordinal);
        Assert.Contains("beta", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TryInvoke_WithNoNameArgument_AsksForOneRatherThanThrowing()
    {
        var result = Loader(Skill("alpha")).TryInvoke(Call(null), []);

        Assert.NotNull(result);
        Assert.Contains("name", result!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A SHORT ACK, NOT THE BODY AGAIN. A model that forgets what it loaded is likely — the body
    /// drifts far up the context — and re-sending a 3k document puts two copies in the window.
    /// </summary>
    [Fact]
    public void TryInvoke_ASecondTime_AcknowledgesRatherThanResendingTheBody()
    {
        var loader = Loader(Skill("rtl-aware-development"));
        var messages = new List<ChatMessage>
        {
            Result(loader.TryInvoke(Call("rtl-aware-development"), [])!),
        };

        var again = loader.TryInvoke(Call("rtl-aware-development"), messages);

        Assert.NotNull(again);
        Assert.Contains("already loaded", again!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Do the thing.", again, StringComparison.Ordinal);
    }

    /// <summary>
    /// IT MUST STILL RETURN SOMETHING. Every tool call needs its result message, or the assistant
    /// message that made it is left holding an unanswered call — the orphan that 400s a session
    /// permanently. "Already loaded" is a legitimate answer; silence is a broken conversation.
    /// </summary>
    [Fact]
    public void TryInvoke_ASecondTime_IsNeverSilent()
    {
        var loader = Loader(Skill("alpha"));
        var messages = new List<ChatMessage> { Result(loader.TryInvoke(Call("alpha"), [])!) };

        Assert.False(string.IsNullOrWhiteSpace(loader.TryInvoke(Call("alpha"), messages)));
    }

    /// <summary>
    /// THE FLIP-BACK, and the subtlest thing in this feature. Compaction removes the body; the model
    /// still believes it is loaded and asks again. Answering "already loaded" then would be a lie
    /// that leaves it with nothing — so the answer is derived from the window, not from a set of
    /// names that only ever grows.
    /// </summary>
    [Fact]
    public void TryInvoke_AfterCompactionRemovedTheBody_ReturnsTheBodyAgain()
    {
        var loader = Loader(Skill("rtl-aware-development"));
        var messages = new List<ChatMessage>
        {
            Result(loader.TryInvoke(Call("rtl-aware-development"), [])!),
        };

        // What compaction does: the older half is replaced by one assistant summary.
        messages.Clear();
        messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "[earlier conversation, summarised: read files, loaded a skill]",
        });

        var again = loader.TryInvoke(Call("rtl-aware-development"), messages);

        Assert.Contains("Do the thing.", again!, StringComparison.Ordinal);
        Assert.DoesNotContain("already loaded", again, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ack must not satisfy the next scan. If it did, one load followed by two asks would leave
    /// the conversation asserting a body is present when only acknowledgements are.
    /// </summary>
    [Fact]
    public void TryInvoke_TheAcknowledgementItself_DoesNotCountAsALoadedBody()
    {
        var loader = Loader(Skill("alpha"));

        // Load once, then ask again WITH the body present — that second answer is the ack.
        var body = loader.TryInvoke(Call("alpha"), [])!;
        var ack = loader.TryInvoke(Call("alpha"), [Result(body)])!;

        Assert.Contains("already loaded", ack, StringComparison.OrdinalIgnoreCase);

        // Now compaction takes the body but leaves the ack — the shape that would fool a scan
        // matching anything a load ever produced. The next call must load for real.
        var next = loader.TryInvoke(Call("alpha"), [Result(ack, id: "call-2")]);

        Assert.Contains("Do the thing.", next!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Filters on ToolCallId, never on Role — the wire builders overwrite Role, and Anthropic emits a
    /// tool result as a "user" turn. A scan keyed on Role would miss every body on that provider.
    /// </summary>
    [Fact]
    public void TryInvoke_RecognisesALoadedBody_WhateverRoleTheWireBuilderGaveIt()
    {
        var loader = Loader(Skill("alpha"));
        var body = loader.TryInvoke(Call("alpha"), [])!;

        var asAnthropicSendsItBack = new ChatMessage
        {
            Role = "user", Content = body, ToolCallId = "call-1",
        };

        var again = loader.TryInvoke(Call("alpha"), [asAnthropicSendsItBack]);

        Assert.Contains("already loaded", again!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An assistant message that merely mentions the marker is not a loaded body — only a real tool
    /// result is. Otherwise a summary quoting the marker would convince the loader a skill is present.
    /// </summary>
    [Fact]
    public void TryInvoke_AnAssistantMessageQuotingTheMarker_IsNotALoadedBody()
    {
        var loader = Loader(Skill("alpha"));
        var pretender = new ChatMessage
        {
            Role = "assistant",
            Content = "[skill: alpha]\nI think I loaded this earlier.",
        };

        var result = loader.TryInvoke(Call("alpha"), [pretender]);

        Assert.Contains("Do the thing.", result!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryInvoke_WithAnEmptyCatalog_SaysSoRatherThanListingNothing()
    {
        var result = Loader().TryInvoke(Call("anything"), []);

        Assert.Contains("No skills are available", result!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A SKILL'S OTHER FILES ARE NAMED BY ABSOLUTE PATH. Published skills carry reference documents
    /// and link them the way markdown does — [references/patterns.md](references/patterns.md) —
    /// which is relative to a directory the model cannot see. Both skills in this repository do
    /// exactly that, under a heading reading "Load References".
    /// </summary>
    [Fact]
    public void TryInvoke_ListsTheSkillsOtherFiles_ByAbsolutePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxa-ref-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "references"));
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\n---\nbody");
            File.WriteAllText(Path.Combine(dir, "references", "patterns.md"), "patterns");
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");

            var loader = new SkillLoader(() => new SkillCatalogResult(
                [new SkillInfo("xunit", "Use when testing.", dir, "# Body\n\nDo the thing.")],
                [], dir));

            var result = loader.TryInvoke(Call("xunit"), [])!;

            Assert.Contains(Path.Combine(dir, "references", "patterns.md"), result, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(dir, "manifest.json"), result, StringComparison.Ordinal);

            // NOT the SKILL.md — its content is already in this very message, and listing it invites
            // a re-read of what the model is holding.
            Assert.DoesNotContain(Path.Combine(dir, "SKILL.md"), result, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A skill with nothing beside its SKILL.md says nothing about files.</summary>
    [Fact]
    public void TryInvoke_WithNoOtherFiles_AddsNoFileList()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxa-noref-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\n---\nbody");

            var loader = new SkillLoader(() => new SkillCatalogResult(
                [new SkillInfo("bare", "Use when testing.", dir, "# Body")], [], dir));

            Assert.DoesNotContain("files (", loader.TryInvoke(Call("bare"), [])!, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A skill shipping forty documents must not spend more of the window on its own file listing
    /// than on its instructions — and this text is a tool result, re-sent on every later turn.
    /// </summary>
    [Fact]
    public void TryInvoke_WithManyFiles_StopsListingAndSaysThereAreMore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxa-many-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\n---\nbody");
            for (var i = 0; i < 40; i++)
                File.WriteAllText(Path.Combine(dir, $"ref{i:D2}.md"), "x");

            var loader = new SkillLoader(() => new SkillCatalogResult(
                [new SkillInfo("big", "Use when testing.", dir, "# Body")], [], dir));

            var result = loader.TryInvoke(Call("big"), [])!;

            Assert.Contains("and more in this directory", result, StringComparison.Ordinal);
            Assert.DoesNotContain("ref39.md", result, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A directory that cannot be listed still loads — the body is the substance.</summary>
    [Fact]
    public void TryInvoke_WhenTheDirectoryIsGone_StillReturnsTheBody()
    {
        var loader = new SkillLoader(() => new SkillCatalogResult(
            [new SkillInfo("ghost", "Use when testing.", "/nope/not/here", "# Body\n\nDo the thing.")],
            [], "/nope"));

        Assert.Contains("Do the thing.", loader.TryInvoke(Call("ghost"), [])!, StringComparison.Ordinal);
    }

    /// <summary>Names are matched case-insensitively: a model that title-cases a name meant the skill.</summary>
    [Fact]
    public void TryInvoke_MatchesTheNameCaseInsensitively()
    {
        var result = Loader(Skill("rtl-aware-development")).TryInvoke(Call("RTL-Aware-Development"), []);

        Assert.Contains("Do the thing.", result!, StringComparison.Ordinal);
    }
}
