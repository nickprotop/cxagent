using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// TASK 11: the classifier call can start the moment a tool call is PARSED, before it runs — often
/// well ahead of the gate that actually needs the verdict. <see cref="ActionClassifier.Speculate"/>
/// warms Task 10's cache with that early call so the real, gated <c>JudgeAsync</c> can find its
/// answer already sitting there instead of paying the 10-second round trip itself.
///
/// <para>THE ONLY CORRECTNESS ARGUMENT IS <see cref="ActionClassifier.CacheKeyFor"/>. Speculation and
/// the real request must agree on "the same action" through exactly one definition, or a verdict
/// computed for one action could be handed to a different one. A speculative call started at parse
/// time may run on PRE-SUBSTITUTION parameters — <c>PermissionGatedExecutor</c> only sees the request
/// after <c>{{job.key}}</c> expansion — so if the content differs by the time the gate asks, the
/// cache key must differ too, and the speculative entry must simply not be found.</para>
/// </summary>
public class SpeculativeClassifierTests
{
    private static PermissionRequest FileWrite(string path) =>
        new(PermissionKind.FileWrite, path, path);

    [Fact]
    public void ASpeculatedVerdictIsReusedByTheRealRequest()
    {
        var provider = new CountingProvider("ALLOW");
        var classifier = new ActionClassifier(provider);
        var request = FileWrite("/tmp/a.txt");

        classifier.Speculate(request, default);
        _ = classifier.JudgeAsync(request, default).Result;

        Assert.Equal(1, provider.Calls);   // the speculative call served the real one
    }

    [Fact]
    public void ASPECULATIONFORADIFFERENTACTIONISDISCARDED()
    {
        // THE VERDICT MUST DESCRIBE THE EXACT ACTION. A speculative call may run on parameters that
        // change before execution — PermissionGatedExecutor receives them AFTER {{job.key}} substitution —
        // so "the same action" is the Task 10 hash, not the subject alone.
        var provider = new CountingProvider("ALLOW");
        var classifier = new ActionClassifier(provider);

        classifier.Speculate(FileWrite("/tmp/a.txt") with { Facts = new() { Diff = "before" } }, default);
        _ = classifier.JudgeAsync(FileWrite("/tmp/a.txt") with { Facts = new() { Diff = "after" } }, default).Result;

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void ASpeculationThatThrowsNeverSurfaces()
    {
        // Nothing awaits it, so an exception has nowhere to go but the render loop. It must be swallowed
        // into the same "ask" the synchronous path uses.
        var classifier = new ActionClassifier(new ThrowingProvider());

        classifier.Speculate(FileWrite("/tmp/a.txt"), default);   // must not throw

        Assert.Equal(ClassifierVerdict.Ask,
            classifier.JudgeAsync(FileWrite("/tmp/a.txt"), default).Result.Verdict);
    }

    // ---- fakes ------------------------------------------------------------------------------

    private sealed class CountingProvider(string? reply) : ILlmProvider
    {
        public int Calls { get; private set; }

        public string ProviderId => "counting";
        public string DisplayName => "Counting";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmResponse { Text = reply });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
