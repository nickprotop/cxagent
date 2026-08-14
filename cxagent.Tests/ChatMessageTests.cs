using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

public class ChatMessageTests
{
    /// <summary>
    /// THE REGENERATED MESSAGE NEEDS A PROPERTY, NOT A STRING MATCH. The task list is rewritten
    /// every turn, so the old copy must be found and replaced — and finding it by its rendered text
    /// would delete a user message that happened to quote the plan back.
    /// </summary>
    [Fact]
    public void IsTaskList_DefaultsToFalse()
    {
        var m = new ChatMessage { Role = "user", Content = "hello" };
        Assert.False(m.IsTaskList);
    }

    [Fact]
    public void IsTaskList_IsSettable()
    {
        var m = new ChatMessage { Role = "user", Content = "plan", IsTaskList = true };
        Assert.True(m.IsTaskList);
    }
}
