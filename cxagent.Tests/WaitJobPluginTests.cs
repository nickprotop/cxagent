using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Builtin;
using Xunit;

namespace CxAgent.Tests;

public class WaitJobPluginTests
{
    private static JobParameters P(params (string k, object? v)[] kv)
        => new(kv.ToDictionary(x => x.k, x => x.v));

    [Fact]
    public async Task Execute_TimedWait_SucceedsAfterDelay()
    {
        var start = DateTimeOffset.UtcNow;
        var r = await new WaitJobPlugin().ExecuteAsync(
            P(("seconds", 0.1)), new CollectingContext(), CancellationToken.None);
        Assert.True(r.Success);
        Assert.True(DateTimeOffset.UtcNow - start >= TimeSpan.FromMilliseconds(80));
    }

    [Fact]
    public async Task Execute_ManualCondition_SucceedsImmediately_InHeadless()
    {
        var r = await new WaitJobPlugin().ExecuteAsync(
            P(("until_condition", "manual")), new CollectingContext(), CancellationToken.None);
        Assert.True(r.Success);   // headless P3: manual wait resolves immediately (real click is P5)
    }

    [Fact]
    public void Validate_RejectsEmptyParams()
    {
        var v = new WaitJobPlugin().Validate(P());
        Assert.False(v.IsValid);
    }
}
