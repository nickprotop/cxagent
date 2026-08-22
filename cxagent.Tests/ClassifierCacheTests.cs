using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE CACHE KEY IS A SECURITY PROPERTY, NOT AN OPTIMISATION. Keying on (kind, subject) alone lets a
/// benign first write to a path cache ALLOW, and a later overwrite of the SAME path with DIFFERENT
/// content reuse that verdict without the classifier ever seeing its diff — defeating the
/// capped-diff defence entirely. <see cref="DIFFERENT_CONTENT_TO_THE_SAME_PATH_IS_A_DIFFERENT_ACTION"/>
/// is the regression test for exactly that hole; see <see cref="ActionClassifier"/> for the key.
/// </summary>
public class ClassifierCacheTests
{
    private static PermissionRequest FileWrite(string path) =>
        new(PermissionKind.FileWrite, path, path);

    [Fact]
    public void TheSameActionTwiceInATurn_CallsTheModelOnce()
    {
        var provider = new CountingProvider("ALLOW");
        var classifier = new ActionClassifier(provider);
        var request = FileWrite("/tmp/a.txt");

        _ = classifier.JudgeAsync(request, default).Result;
        _ = classifier.JudgeAsync(request, default).Result;

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public void DIFFERENT_CONTENT_TO_THE_SAME_PATH_IS_A_DIFFERENT_ACTION()
    {
        // THE CACHE-POISONING HOLE. Keying on (kind, subject) alone lets a benign first write to
        // config.json cache ALLOW, and a later malicious overwrite of the same path reuse it WITHOUT the
        // classifier seeing its diff — defeating the capped-diff defence entirely.
        var provider = new CountingProvider("ALLOW");
        var classifier = new ActionClassifier(provider);

        _ = classifier.JudgeAsync(FileWrite("/tmp/a.txt") with { Facts = new() { Diff = "hello" } }, default).Result;
        _ = classifier.JudgeAsync(FileWrite("/tmp/a.txt") with { Facts = new() { Diff = "rm -rf" } }, default).Result;

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void ResetTurnState_ClearsTheCache()
    {
        var provider = new CountingProvider("ALLOW");
        var classifier = new ActionClassifier(provider);
        var request = FileWrite("/tmp/a.txt");

        _ = classifier.JudgeAsync(request, default).Result;
        classifier.ResetTurnState();
        _ = classifier.JudgeAsync(request, default).Result;

        Assert.Equal(2, provider.Calls);
    }

    /// <summary>Only real verdicts are cached — a provider that throws must be asked again every
    /// time, never a cached ASK standing in for a transient blip a retry might have resolved.</summary>
    [Fact]
    public async Task AFailure_IsNeverCached()
    {
        var provider = new ThrowingCountingProvider();
        var classifier = new ActionClassifier(provider);
        var request = FileWrite("/tmp/a.txt");

        await classifier.JudgeAsync(request, default);
        await classifier.JudgeAsync(request, default);

        Assert.Equal(2, provider.Calls);
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

    private sealed class ThrowingCountingProvider : ILlmProvider
    {
        public int Calls { get; private set; }

        public string ProviderId => "throwing-counting";
        public string DisplayName => "ThrowingCounting";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromException<LlmResponse>(new HttpRequestException("down"));
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
