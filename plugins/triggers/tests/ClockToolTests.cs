using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Plugins.Triggers.Tests;

/// <summary>The four clock tools, against a fake client that records what was submitted.</summary>
[Collection("TriggerStore")]
public class ClockToolTests : IDisposable
{
    private readonly string _session = "session-" + Guid.NewGuid().ToString("N");
    private readonly FakeContext _context;
    private readonly TriggersPlugin _plugin = new();

    public ClockToolTests()
    {
        _context = new FakeContext(_session);
        _plugin.Load(_context, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() => TriggerStore.SweepSession(_session);

    private sealed class FakeClient : IPluginClient
    {
        public List<string> Submitted { get; } = [];

        public Task<SubmitResult> Submit(string goal, bool wantResult = false,
            CancellationToken ct = default)
        {
            Submitted.Add(goal);
            return Task.FromResult(new SubmitResult(true, null, null));
        }
    }

    private sealed class FakeContext(string sessionId) : IPluginContext
    {
        public string WorkingDirectory => Path.GetTempPath();
        public System.Text.Json.JsonElement Settings =>
            System.Text.Json.JsonDocument.Parse("{}").RootElement;
        public int HostContract => 3;
        public string HostVersion => "0.0.0";
        public IPluginLogger Logger { get; } = new NullLogger();
        public IPluginClient? Client { get; } = new FakeClient();
        public string? SessionId => sessionId;
        public CancellationToken Lifetime => CancellationToken.None;
        public void RegisterChildProcess(int processId) { }

        private sealed class NullLogger : IPluginLogger
        {
            public void Log(string message) { }
        }
    }

    private async Task<JobResult> Call(string tool, Dictionary<string, object?> args) =>
        await _plugin.Invoke(tool, new JobParameters(args), null!, CancellationToken.None);

    [Fact]
    public async Task A_wake_is_accepted_and_answers_with_its_id()
    {
        var result = await Call("trigger_wake",
            new() { ["after"] = "20m", ["prompt"] = "check the deploy" });

        Assert.True(result.Success);
        Assert.Single(TriggerStore.For(_session));
        Assert.Contains("1", result.Output["content"]!.ToString()!);
    }

    /// <summary>
    /// THE REFUSAL NAMES WHICH RULE WAS BROKEN, because the caller is a model composing a call and a
    /// bare "invalid" leaves it guessing which of three grammars it got wrong.
    /// </summary>
    [Fact]
    public async Task Setting_two_when_fields_is_refused_saying_so()
    {
        var result = await Call("trigger_wake",
            new() { ["after"] = "20m", ["every"] = "0 9 * * *", ["prompt"] = "x" });

        Assert.False(result.Success);
        Assert.Contains("exactly one", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(TriggerStore.For(_session));
    }

    [Fact]
    public async Task An_empty_list_says_so_rather_than_answering_nothing()
    {
        var result = await Call("trigger_list", new());

        Assert.True(result.Success);
        Assert.Contains("no triggers", result.Output["content"]!.ToString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_listing_shows_the_id_when_it_fires_and_the_prompt()
    {
        await Call("trigger_wake", new() { ["after"] = "20m", ["prompt"] = "check the deploy" });

        var listing = (await Call("trigger_list", new())).Output["content"]!.ToString()!;

        Assert.Contains("1", listing);
        Assert.Contains("20m", listing);
        Assert.Contains("check the deploy", listing);
    }

    [Fact]
    public async Task Cancelling_removes_it_and_says_which()
    {
        await Call("trigger_wake", new() { ["after"] = "20m", ["prompt"] = "x" });

        var result = await Call("trigger_cancel", new() { ["id"] = 1 });

        Assert.True(result.Success);
        Assert.Empty(TriggerStore.For(_session));
    }

    [Fact]
    public async Task Cancelling_an_id_nobody_holds_is_refused_rather_than_silently_fine()
    {
        var result = await Call("trigger_cancel", new() { ["id"] = 99 });

        Assert.False(result.Success);
        Assert.Contains("99", result.ErrorMessage!);
    }

    [Fact]
    public async Task Updating_keeps_the_id()
    {
        await Call("trigger_wake", new() { ["after"] = "20m", ["prompt"] = "old" });

        var result = await Call("trigger_update",
            new() { ["id"] = 1, ["after"] = "2h", ["prompt"] = "new" });

        Assert.True(result.Success);
        var left = TriggerStore.For(_session).Single();
        Assert.Equal(1, left.Id);
        Assert.Equal("new", left.Prompt);
    }

    /// <summary>
    /// THE FIRE SUBMITS INTO ITS OWN SESSION, which the reach decision already settled: the tool was
    /// called IN a session and the wake goes back into that same one.
    /// </summary>
    [Fact]
    public async Task A_due_trigger_submits_its_prompt()
    {
        await Call("trigger_wake", new() { ["after"] = "1s", ["prompt"] = "wake up" });

        await _plugin.FireDue(DateTimeOffset.Now.AddMinutes(1));

        var client = (FakeClient)_context.Client!;
        Assert.Equal(["wake up"], client.Submitted);
    }

    /// <summary>
    /// A ONE-SHOT IS GONE AFTER IT FIRES. Leaving it would submit the same goal on every tick.
    /// </summary>
    [Fact]
    public async Task A_fired_one_shot_does_not_fire_again()
    {
        await Call("trigger_wake", new() { ["after"] = "1s", ["prompt"] = "once" });

        await _plugin.FireDue(DateTimeOffset.Now.AddMinutes(1));
        await _plugin.FireDue(DateTimeOffset.Now.AddMinutes(2));

        Assert.Single(((FakeClient)_context.Client!).Submitted);
    }
}
