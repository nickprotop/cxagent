using CxAgent.Core.Models;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

public class SkillModelViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillview-" + Guid.NewGuid().ToString("N"));
    public SkillModelViewTests() => Directory.CreateDirectory(Path.Combine(_root, ".git"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (Exception) { } }

    private void Skill(string folder, string name, string extra = "")
    {
        var dir = Path.Combine(folder, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\ndescription: {name}\n{extra}---\nbody\n");
    }

    [Fact]
    public void Find_IsTheModelView_PerName_AcrossFolders()
    {
        var config = Path.Combine(_root, "config");
        Skill(Path.Combine(_root, ".cxagent", "skills"), "one");
        Skill(Path.Combine(config, "skills"), "two");
        Skill(Path.Combine(_root, ".cxagent", "skills"), "mine", "disable-model-invocation: true\n");

        var found = SkillCatalog.Find(_root, config);

        Assert.Equal(["one", "two"], found.Skills.Select(s => s.Name).ToArray());
    }

    /// <summary>Claude Code's skills are written for its tools, not these: not offered to the model.</summary>
    [Fact]
    public void ClaudeSkills_AreNotRead()
    {
        Skill(Path.Combine(_root, ".claude", "skills"), "from-claude");

        Assert.Empty(SkillCatalog.Find(_root).Skills);
    }

    /// <summary>A skill the user typed is loaded: the skill tool must not send it again.</summary>
    [Fact]
    public void LoadedIn_ReadsTheMarker_InAUserMessage()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "[skill: deploy]\ndirectory: /x\n\nbody" },
            new() { Role = "assistant", Content = "[skill: quoted] is not a load" },
        };

        Assert.Equal(["deploy"], SkillLoader.LoadedIn(messages));
    }
}
