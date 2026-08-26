using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE PROMPT MUST BE A PURE FUNCTION OF ITS FACTS, NOT OF THE ORDER THEY ARRIVED IN.
///
/// <para>Everything before the first changed byte is served from the provider's prefix cache — see
/// <see cref="McpPromptStabilityTests"/> for what a mid-conversation rewrite costs. The collections
/// feeding this prompt come from a filesystem enumeration, a dictionary and a plugin registry, none
/// of which promises an order; a list that arrives shuffled between runs would churn the prefix on
/// every session for nothing.</para>
///
/// <para>THREE PLACES ALREADY SORT, AND EACH SAYS WHY IN A COMMENT: skills in
/// <c>SkillCatalog.ReadDirectory</c>, MCP servers and plugins in <c>SystemPrompt</c> itself. A
/// comment cannot fail a build. This is the test that does — a fourth collection appended later
/// without a sort fails here rather than quietly costing every user a cold prefix.</para>
/// </summary>
public class SystemPromptStabilityTests
{
    /// <summary>
    /// Everything the prompt renders, populated: an empty context would pass every assertion here
    /// while proving nothing about the sections that actually carry collections.
    /// </summary>
    private static SystemPromptContext Populated() => new(
        WorkingDirectory: "/work/project",
        IsGitRepo: true,
        Platform: "linux",
        Today: new DateOnly(2026, 8, 26),
        ModelId: "test-model")
    {
        McpInstructions = new Dictionary<string, string>
        {
            ["alpha"] = "The first server.",
            ["beta"] = "The second server.",
            ["gamma"] = "The third server.",
        },
        PluginInstructions =
        [
            new PluginInstructions("a-plugin", ["a_one", "a_two"], "Guidance from the first."),
            new PluginInstructions("b-plugin", ["b_one"], "Guidance from the second."),
        ],
        Skills =
        [
            new SkillInfo("apples", "About apples", "/skills/apples", "body"),
            new SkillInfo("bananas", "About bananas", "/skills/bananas", "body"),
            new SkillInfo("cherries", "About cherries", "/skills/cherries", "body"),
        ],
        CanSpawn = true,
        CanAskUser = true,
    };

    /// <summary>
    /// The same facts twice must give the same bytes. A hash-ordered set or a clock read anywhere in
    /// the builder shows up here and nowhere else, because every other test asserts on a substring
    /// rather than on the whole prompt.
    /// </summary>
    [Fact]
    public void TheSameFactsBuildTheSamePrompt()
    {
        var built = Enumerable.Range(0, 8).Select(_ => SystemPrompt.Build(Populated())).ToList();

        Assert.Single(built.Distinct());
    }

    /// <summary>
    /// REVERSED, NOT SHUFFLED. A shuffle can coincidentally reproduce the original order and pass a
    /// test that should have failed; reversing three or more items never can.
    /// </summary>
    [Fact]
    public void ReorderingWhatFeedsThePromptDoesNotChangeIt()
    {
        var forward = Populated();
        var reversed = forward with
        {
            McpInstructions = forward.McpInstructions.Reverse()
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            PluginInstructions = forward.PluginInstructions.Reverse().ToList(),
            Skills = forward.Skills.Reverse().ToList(),
        };

        var a = SystemPrompt.Build(forward);
        var b = SystemPrompt.Build(reversed);

        // NAMES THE BYTE, because "the prompts differ" over 7,000 characters is a fact nobody can
        // act on. The offset points at the section whose sort went missing.
        if (a != b)
        {
            var at = Enumerable.Range(0, Math.Min(a.Length, b.Length))
                .FirstOrDefault(i => a[i] != b[i], Math.Min(a.Length, b.Length));
            Assert.Fail(
                $"Reordering the prompt's inputs changed it at char {at} of {a.Length} — "
                + $"{(double)at / a.Length:P1} of the prefix survives. A collection reaching the "
                + $"prompt unsorted churns the cache for every user.\n"
                + $"  forward:  {Excerpt(a, at)}\n"
                + $"  reversed: {Excerpt(b, at)}");
        }
    }

    /// <summary>
    /// Each collection alone, so a failure names which one lost its sort rather than leaving the
    /// reader to work it out from a character offset.
    /// </summary>
    [Theory]
    [InlineData("MCP servers")]
    [InlineData("plugins")]
    [InlineData("skills")]
    public void EachCollectionSortsIndependently(string which)
    {
        var forward = Populated();
        var reversed = which switch
        {
            "MCP servers" => forward with
            {
                McpInstructions = forward.McpInstructions.Reverse()
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
            },
            "plugins" => forward with { PluginInstructions = forward.PluginInstructions.Reverse().ToList() },
            _ => forward with { Skills = forward.Skills.Reverse().ToList() },
        };

        Assert.True(SystemPrompt.Build(forward) == SystemPrompt.Build(reversed),
            $"Reversing the {which} changed the prompt: that section reaches it unsorted.");
    }

    private static string Excerpt(string text, int at) =>
        text.Substring(Math.Max(0, at - 40), Math.Min(80, text.Length - Math.Max(0, at - 40)))
            .Replace("\n", "\\n");
}
