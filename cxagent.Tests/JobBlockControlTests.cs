using CxAgent.Core.Models;
using CxAgent.UI;
using SharpConsoleUI.Themes;   // ColorRole
using Xunit;

namespace CxAgent.Tests;

public class JobBlockControlTests
{
    private static Job J(JobState state) => new()
    {
        Id = "j1", AgentId = "g", JobType = "shell", DisplayName = "Step 1",
        State = state, CreatedAt = DateTimeOffset.UtcNow
    };

    [Theory]
    [InlineData(JobState.Running, ColorRole.Info)]
    [InlineData(JobState.Succeeded, ColorRole.Success)]
    [InlineData(JobState.Failed, ColorRole.Danger)]
    [InlineData(JobState.Paused, ColorRole.Warning)]
    [InlineData(JobState.Pending, ColorRole.Default)]
    [InlineData(JobState.Queued, ColorRole.Default)]
    [InlineData(JobState.Cancelled, ColorRole.Default)]
    [InlineData(JobState.Skipped, ColorRole.Default)]
    public void RoleFor_MapsStateToColorRole(JobState state, ColorRole expected)
        => Assert.Equal(expected, JobBlockControl.RoleFor(state));

    [Fact]
    public void Update_SetsColorRole_AndTitle_AndJobId()
    {
        var block = new JobBlockControl();
        block.Update(J(JobState.Running));
        Assert.Equal("j1", block.JobId);
        Assert.Equal(ColorRole.Info, block.ColorRole);
        Assert.Contains("Step 1", block.Title);   // title carries the job name
    }

    [Fact]
    public void Update_Transition_Running_To_Succeeded_UpdatesRole()
    {
        var block = new JobBlockControl();
        block.Update(J(JobState.Running));
        Assert.Equal(ColorRole.Info, block.ColorRole);
        block.Update(J(JobState.Succeeded));
        Assert.Equal(ColorRole.Success, block.ColorRole);
    }
}
