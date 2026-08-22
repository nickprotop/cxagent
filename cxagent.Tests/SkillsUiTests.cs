using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using CxAgent.Core.Skills;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a user actually sees: which skills are in force, and which SKILL.md was refused. The
/// refusals are the reason /skills exists at all — a broken file is invisible to the model, and
/// nothing else in the app would ever mention it.
/// </summary>
[Collection("working-directory")]
public class SkillsUiTests
{
    private static SkillInfo Skill(string name, string description = "Use when testing.") =>
        new(name, description, $"/tmp/skills/{name}", "body");

    // RENDERS, RATHER THAN PRINTING THROUGH A DOUBLE. The command took a transcript writer and this
    // supplied a fake one to read back what it wrote; a listing that is pure text needs neither.
    private static string Render(SkillCatalogResult result) =>
        new SkillsCommand(() => result).Render();

    [Fact]
    public void Skills_ListsEachSkillWithItsDescription_AndTheDirectoryTheyCameFrom()
    {
        var text = Render(new SkillCatalogResult(
            [Skill("rtl-aware-development", "Use when implementing RTL/LTR behaviour.")],
            [], "/repo/.cxagent/skills"));

        Assert.Contains("rtl-aware-development", text, StringComparison.Ordinal);
        // VERBATIM: a user debugging "why was this never loaded?" must read what the model read.
        Assert.Contains("Use when implementing RTL/LTR behaviour.", text, StringComparison.Ordinal);
        Assert.Contains("/repo/.cxagent/skills", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE WHOLE REASON THE COMMAND EXISTS. A SKILL.md with broken frontmatter is invisible to the
    /// model; without this the user has a file that does nothing and no error anywhere.
    /// </summary>
    [Fact]
    public void Skills_NamesEverySkippedFile_AndWhy()
    {
        var text = Render(new SkillCatalogResult(
            [Skill("good")],
            [new SkillProblem("/repo/.agents/skills/bad/SKILL.md", "no description")],
            "/repo/.cxagent/skills"));

        Assert.Contains("/repo/.agents/skills/bad/SKILL.md", text, StringComparison.Ordinal);
        Assert.Contains("no description", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// NO WINNER IS NOT AN ERROR, and must not claim a directory is in use. This is what a first
    /// attempt at writing a skill looks like, so it is the case that most needs to read well.
    /// </summary>
    [Fact]
    public void Skills_WithEverythingMalformed_SaysNoneLoaded_WithoutNamingADirectoryAsInUse()
    {
        var text = Render(new SkillCatalogResult(
            [],
            [
                new SkillProblem("/repo/.cxagent/skills/a/SKILL.md", "no frontmatter"),
                new SkillProblem("/repo/.agents/skills/b/SKILL.md", "no description"),
            ],
            SourceDirectory: null));

        Assert.Contains("No skills loaded", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("in use", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no frontmatter", text, StringComparison.Ordinal);
        Assert.Contains("no description", text, StringComparison.Ordinal);
    }

    /// <summary>Nothing at all: say where to put one rather than printing an empty heading.</summary>
    [Fact]
    public void Skills_WithNothingFound_SaysWhereToPutOne()
    {
        var text = Render(new SkillCatalogResult([], [], null));

        Assert.Contains("SKILL.md", text, StringComparison.Ordinal);
        Assert.Contains(".cxagent/skills", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The panel reports what is IN FORCE, derived from the window — so it stops reporting a skill
    /// the moment compaction removes its body, which is the silent behaviour change it exists to
    /// make visible.
    /// </summary>
    [Fact]
    public void LoadedIn_ReportsABodyInTheWindow_AndForgetsItOnceRemoved()
    {
        var body = new ChatMessage
        {
            Role = "tool",
            Content = "[skill: rtl-aware-development]\ndirectory: /tmp\n\n# Body",
            ToolCallId = "call-1",
        };

        Assert.Equal(["rtl-aware-development"], SkillLoader.LoadedIn([body]));

        // What compaction leaves behind: one assistant summary, no bodies.
        var afterCompaction = new ChatMessage
        {
            Role = "assistant",
            Content = "[earlier conversation, summarised: loaded a skill, read some files]",
        };

        Assert.Empty(SkillLoader.LoadedIn([afterCompaction]));
    }

    /// <summary>
    /// THE FINISHED ROW IS THE TRAP. The completion path builds a SECOND list and REPLACES
    /// ProgressBody with it, so a change made only to the live `facts` passes a live-row assertion
    /// while the finished row silently drops the line — and the finished row is the surface that
    /// outlives the run, which is exactly when "why did it answer that way?" gets asked.
    ///
    /// <para>It also cannot read the child's agent by then: this feature's own argument is that the
    /// child's context is gone once it exits, so the names must have been CAPTURED during the run.</para>
    /// </summary>
    [Fact]
    public async Task AWorkersFinishedRow_StillNamesTheSkillItLoaded_AfterItHasExited()
    {
        var root = Path.Combine(Path.GetTempPath(), "cxa-skui-" + Guid.NewGuid().ToString("N"));
        var here = Directory.GetCurrentDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var skill = Directory.CreateDirectory(
                Path.Combine(root, ".cxagent", "skills", "deployment")).FullName;
            File.WriteAllText(Path.Combine(skill, "SKILL.md"),
                "---\nname: deployment\ndescription: Use when deploying.\n---\n\nShip carefully.\n");

            // Discovery reads the process working directory, which is what the agent will see.
            Directory.SetCurrentDirectory(root);

            var provider = new MockLlmProvider();
            provider.EnqueueResponse(new LlmResponse
            {
                Text = "", StopReason = "tool_use",
                ToolCalls = [new ToolCall
                {
                    Id = "call-1", Name = "agent",
                    Arguments = System.Text.Json.JsonDocument.Parse(
                        """{"description":"deploy it","prompt":"deploy it"}""").RootElement,
                }],
            });
            provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

            // The CHILD loads the skill — the case that matters, since its context is invisible.
            var childProvider = new MockLlmProvider();
            childProvider.EnqueueResponse(new LlmResponse
            {
                Text = "", StopReason = "tool_use",
                ToolCalls = [new ToolCall
                {
                    Id = "t1", Name = "skill",
                    Arguments = System.Text.Json.JsonDocument.Parse(
                        """{"name":"deployment"}""").RootElement,
                }],
            });
            childProvider.EnqueueResponse(new LlmResponse { Text = "child done", StopReason = "end_turn" });

            var jobs = new NullJobPanel();
            var parent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
                new RecordingSink(), jobs, logs: null, maxTurns: 50,
                spawner: new SubAgentSpawner(new SubAgentFactory(
                    new SubAgentFactory.SubAgentRuntime
                    {
                        Provider = childProvider,
                        Executors = JobRegistry.CreateWithBuiltins(),
                        Ledger = new TokenLedger(),
                        MaxTurns = 50,
                        CompressAbove = 40_000,
                        ContextWindow = 200_000,
                    })))
            {
                Mode = AgentMode.FanOut,
            };

            await parent.SendAsync("delegate", CancellationToken.None);

            // The child has exited and its context is gone — this reads the captured copy.
            var row = Assert.Single(jobs.Jobs);
            Assert.Contains("skills: deployment", row.ProgressBody!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(here);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// THE TOOL MUST ACTUALLY BE OFFERED. A live drive found the model hunting the SKILL.md down with
    /// list_files and read_file instead of calling load_skill — the catalog was in its prompt, so it
    /// knew the skill existed, and it did the only thing available to it. Everything downstream of
    /// loading is unreachable if the definition never reaches the request.
    /// </summary>
    [Fact]
    public async Task TheLoadSkillTool_IsOfferedToTheModel_WhenSkillsExist()
    {
        var root = Path.Combine(Path.GetTempPath(), "cxa-offer-" + Guid.NewGuid().ToString("N"));
        var here = Directory.GetCurrentDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var skill = Directory.CreateDirectory(
                Path.Combine(root, ".cxagent", "skills", "deployment")).FullName;
            File.WriteAllText(Path.Combine(skill, "SKILL.md"),
                "---\nname: deployment\ndescription: Use when deploying.\n---\n\nShip carefully.\n");
            Directory.SetCurrentDirectory(root);

            var provider = new ToolCapturingProvider();
            var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
                new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2);

            await agent.SendAsync("do something", CancellationToken.None);

            Assert.Contains("skill", provider.LastTools.Select(t => t.Name));
        }
        finally
        {
            Directory.SetCurrentDirectory(here);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>And withheld when there is nothing to load — every call could only fail.</summary>
    [Fact]
    public async Task TheLoadSkillTool_IsWithheld_WhenThereAreNoSkills()
    {
        var root = Path.Combine(Path.GetTempPath(), "cxa-nooffer-" + Guid.NewGuid().ToString("N"));
        var here = Directory.GetCurrentDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.SetCurrentDirectory(root);

            var provider = new ToolCapturingProvider();
            var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
                new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2);

            await agent.SendAsync("do something", CancellationToken.None);

            Assert.DoesNotContain("skill", provider.LastTools.Select(t => t.Name));
        }
        finally
        {
            Directory.SetCurrentDirectory(here);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Captures the tool definitions the agent actually sent.</summary>
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

    /// <summary>Each skill once, in load order, however many times it was asked for.</summary>
    [Fact]
    public void LoadedIn_ListsEachSkillOnce_InLoadOrder()
    {
        ChatMessage Body(string name, string id) => new()
        {
            Role = "tool", Content = $"[skill: {name}]\n\nbody", ToolCallId = id,
        };

        var loaded = SkillLoader.LoadedIn(
            [Body("zebra", "1"), Body("alpha", "2"), Body("zebra", "3")]);

        Assert.Equal(["zebra", "alpha"], loaded);
    }
}
