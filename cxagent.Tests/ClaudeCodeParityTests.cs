using CxAgent.Core.Commands;
using CxAgent.Core.Permissions;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Command files written the way Claude Code's own documentation writes them, read and expanded
/// unchanged — the promise that a command copied from .claude/commands into .cxagent/commands works
/// here as it did there.
/// </summary>
public class ClaudeCodeParityTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "parity-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _ran = [];
    private readonly List<PermissionRequest> _asked = [];

    public ClaudeCodeParityTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_repo, ".cxagent", "commands"));
    }

    public void Dispose() { try { Directory.Delete(_repo, recursive: true); } catch (Exception) { } }

    private void File_(string name, string text) =>
        File.WriteAllText(Path.Combine(_repo, ".cxagent", "commands", name + ".md"), text);

    private Invocable Find(string name) =>
        InvocableCatalog.Find(new CatalogRoots(_repo, null)).Typed(name)
        ?? throw new Xunit.Sdk.XunitException($"/{name} was not found");

    private Task<string> Expand(Invocable entry, string arguments) =>
        CommandExpander.ExpandAsync(new ExpansionRequest(entry, arguments),
            new ExpansionPorts(_repo, (r, _) => { _asked.Add(r); return Task.FromResult(true); }, _ => { })
            {
                RunShell = (command, _) => { _ran.Add(command); return Task.FromResult(new ShellLineResult(0, false, $"<{command}>")); },
            }, default);

    [Fact]
    public async Task TheCommitCommand_RunsItsShellLinesInOrder_AndIgnoresItsToolGrant()
    {
        File_("commit", """
            ---
            allowed-tools: Bash(git add:*), Bash(git status:*), Bash(git commit:*)
            description: Create a git commit
            ---

            ## Context

            - Current git status: !`git status`
            - Current git diff (staged and unstaged changes): !`git diff HEAD`
            - Current branch: !`git branch --show-current`
            - Recent commits: !`git log --oneline -10`

            ## Your task

            Based on the above changes, create a single git commit.
            """);

        var entry = Find("commit");
        var text = await Expand(entry, "");

        Assert.Equal("Create a git commit", entry.Description);
        Assert.Equal(["allowed-tools"], entry.IgnoredFields);
        Assert.Equal(["git status", "git diff HEAD", "git branch --show-current", "git log --oneline -10"], _ran);
        Assert.Equal(4, _asked.Count);
        Assert.Contains("- Current git status: <git status>", text);
        Assert.EndsWith("create a single git commit.", text);
    }

    [Fact]
    public async Task TheFixIssueCommand_TakesItsArgumentsWhole()
    {
        File_("fix-issue", "Find and fix issue #$ARGUMENTS. Follow these steps: 1. Understand the issue described in the ticket 2. Locate the relevant code in our codebase");

        var text = await Expand(Find("fix-issue"), "123 high-priority");

        Assert.StartsWith("Find and fix issue #123 high-priority. Follow these steps:", text);
        Assert.DoesNotContain("ARGUMENTS:", text);
    }

    [Fact]
    public async Task APositionalCommand_FillsEachWord()
    {
        File_("review-pr", """
            ---
            argument-hint: [pr-number] [priority] [assignee]
            description: Review pull request
            ---

            Review PR #$1 with priority $2 and assign to $3.
            Focus on security, performance, and code style.
            """);

        var entry = Find("review-pr");
        var text = await Expand(entry, "456 high alice");

        Assert.Equal("[pr-number] [priority] [assignee]", entry.ArgumentHint);
        Assert.StartsWith("Review PR #456 with priority high and assign to alice.", text);
    }
}
