using System.Text.Json;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

public class JobParametersTests
{
    [Fact]
    public void Get_ReturnsValueOfTypeT_WhenAlreadyTyped()
    {
        var p = new JobParameters(new() { ["timeout"] = 30 });
        Assert.Equal(30, p.Get<int>("timeout"));
    }

    [Fact]
    public void Get_ConvertsJsonElement_AfterPersistenceRoundTrip()
    {
        // Simulate a value that came back from SQLite / the LLM as a JsonElement.
        var element = JsonSerializer.Deserialize<JsonElement>("42");
        var p = new JobParameters(new() { ["count"] = element });

        // A blind (int)Values["count"] would throw InvalidCastException here.
        Assert.Equal(42, p.Get<int>("count"));
    }

    [Fact]
    public void Get_ConvertsJsonElementString()
    {
        var element = JsonSerializer.Deserialize<JsonElement>("\"echo hi\"");
        var p = new JobParameters(new() { ["command"] = element });
        Assert.Equal("echo hi", p.Get<string>("command"));
    }

    [Fact]
    public void Get_WithDefault_ReturnsDefaultWhenMissing()
    {
        var p = new JobParameters(new());
        Assert.Equal("origin", p.Get("remote", "origin"));
    }
}
