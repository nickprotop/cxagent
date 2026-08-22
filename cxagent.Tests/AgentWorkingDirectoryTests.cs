using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The agent works in the directory it was GIVEN, not the one the process happens to be in.
///
/// <para>These share the working-directory collection because they move the process cwd to prove
/// the two are independent — see <see cref="WorkingDirectoryCollection"/>.</para>
/// </summary>
[Collection("working-directory")]
public class AgentWorkingDirectoryTests : IDisposable
{
    private readonly string _elsewhere;
    private readonly string _given;
    private readonly string _originalCwd = Directory.GetCurrentDirectory();

    public AgentWorkingDirectoryTests()
    {
        _elsewhere = Path.Combine(Path.GetTempPath(), "cxa-cwd-else-" + Guid.NewGuid().ToString("N"));
        _given = Path.Combine(Path.GetTempPath(), "cxa-cwd-given-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_elsewhere);
        Directory.CreateDirectory(_given);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        foreach (var dir in new[] { _elsewhere, _given })
            try { Directory.Delete(dir, recursive: true); } catch { }

        GC.SuppressFinalize(this);
    }

    private Agent Build(ToolCapturingProvider provider, string workingDir) =>
        new(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 2,
            workingDir: workingDir);

    /// <summary>
    /// THE SYSTEM PROMPT NAMES THE GIVEN DIRECTORY, not the process cwd. Reading the cwd lets an
    /// agent constructed for one folder tell the model it is working in another — silently, and
    /// only when something else has moved the process.
    /// </summary>
    [Fact]
    public async Task TheSystemPrompt_NamesTheDirectoryTheAgentWasGiven()
    {
        Directory.SetCurrentDirectory(_elsewhere);

        var provider = new ToolCapturingProvider();
        await Build(provider, _given).SendAsync("hello", CancellationToken.None);

        Assert.Contains(_given, provider.LastSystemPrompt);
        Assert.DoesNotContain(_elsewhere, provider.LastSystemPrompt);
    }

    /// <summary>
    /// SKILLS COME FROM THE GIVEN DIRECTORY TOO. Discovery walks `.cxagent/skills` upward from where
    /// the agent works — reading it from the process instead meant a skill written for this project
    /// was invisible whenever the process was pointed somewhere else.
    /// </summary>
    [Fact]
    public async Task Skills_AreFoundUnderTheDirectoryTheAgentWasGiven()
    {
        var skillDir = Path.Combine(_given, ".cxagent", "skills", "house-rules");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: house-rules\ndescription: Use when editing anything here.\n---\n\nBe careful.\n");

        Directory.SetCurrentDirectory(_elsewhere);

        var provider = new ToolCapturingProvider();
        await Build(provider, _given).SendAsync("hello", CancellationToken.None);

        Assert.Contains("house-rules", provider.LastSystemPrompt);
    }

    /// <summary>Records the system message the provider was actually sent.</summary>
    private sealed class ToolCapturingProvider : ILlmProvider
    {
        public string LastSystemPrompt { get; private set; } = "";
        public string ProviderId => "capturing";
        public string DisplayName => "Capturing";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            LastSystemPrompt = string.Join("\n",
                messages.Where(m => m.Role == "system").Select(m => m.Content));

            return Task.FromResult(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true, null, r.StopReason);
        }
    }
}
