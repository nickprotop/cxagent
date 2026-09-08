using System.Text.Json;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <see cref="PluginResolver.RuntimeContext.Lifetime"/> is the token a plugin is told to watch for
/// its own session ending — see that property's own doc. This proves the token is a real one that
/// fires on disposal, not <see cref="System.Threading.CancellationToken.None"/> standing in for it.
/// </summary>
public class PluginLifetimeTests
{
    /// <summary>Stands in for a real session handle — this test is about Lifetime, not Submit.</summary>
    private sealed class NullClient : IPluginClient
    {
        public Task<SubmitResult> Submit(string goal, bool wantResult = false, CancellationToken ct = default) =>
            throw new NotSupportedException("not exercised by this test");
    }

    [Fact]
    public void Lifetime_is_cancelled_when_the_context_is_disposed()
    {
        using var doc = JsonDocument.Parse("{}");
        var children = new ChildProcessStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var context = new PluginResolver.RuntimeContext(new PluginResolver.PluginRuntime(
            WorkingDirectory: Path.GetTempPath(), Settings: doc.RootElement, Report: _ => { },
            Children: children, PluginName: "test", SessionId: "s1", Client: new NullClient()));

        Assert.False(context.Lifetime.IsCancellationRequested);
        context.Dispose();
        Assert.True(context.Lifetime.IsCancellationRequested);
    }
}
