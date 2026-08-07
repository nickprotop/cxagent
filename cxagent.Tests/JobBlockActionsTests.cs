using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class JobBlockActionsTests
{
    private static Job J(JobState state) => new()
    {
        Id = "j1", GoalId = "g", PluginType = "shell", DisplayName = "Run tests",
        State = state, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void ShowResources_RendersCpuAndMemory()
    {
        var b = new JobBlockControl();
        b.Update(J(JobState.Running));

        b.ShowResources(new ResourceSnapshot(42.5, 128L * 1024 * 1024, DateTimeOffset.UtcNow));

        Assert.Contains("42", b.ResourceText);
        // Bytes must be rendered human-readably — "134217728" in a job block is unreadable.
        Assert.DoesNotContain("134217728", b.ResourceText);
        Assert.Contains("MB", b.ResourceText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResourceText_IsClearedWhenTheJobLeavesRunning()
    {
        var b = new JobBlockControl();
        b.Update(J(JobState.Running));
        b.ShowResources(new ResourceSnapshot(10, 1024 * 1024, DateTimeOffset.UtcNow));
        Assert.NotEmpty(b.ResourceText);

        b.Update(J(JobState.Succeeded));

        // A finished job showing a frozen live CPU figure reads as though it is still running.
        Assert.True(string.IsNullOrEmpty(b.ResourceText));
    }
}
