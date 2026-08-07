using System.Net;

namespace CxAgent.Core.Llm.Providers;

/// <summary>Transient-retry policy for LLM HTTP calls: retry 429/5xx/network, honor Retry-After, cap attempts.</summary>
public readonly record struct RetryPolicy(int MaxAttempts, TimeSpan BaseDelay)
{
    public static RetryPolicy Default => new(4, TimeSpan.FromSeconds(1));
    public static RetryPolicy NoDelay => new(4, TimeSpan.Zero);
}

public static class LlmHttpRetry
{
    /// <summary>
    /// Sends a request (rebuilt each attempt from requestFactory, since HttpRequestMessage is single-use),
    /// retrying on 429/5xx and network exceptions with capped exponential backoff honoring Retry-After.
    /// A non-retryable status (4xx except 429) or exhausted attempts throws LlmProviderException.
    /// A successful (2xx) or non-retryable response is returned to the caller for body reading.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client, Func<HttpRequestMessage> requestFactory,
        string instanceName, RetryPolicy policy, CancellationToken ct)
    {
        Exception? lastNetworkError = null;
        for (int attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            HttpResponseMessage? resp = null;
            try
            {
                resp = await client.SendAsync(requestFactory(), HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                lastNetworkError = ex;
                if (attempt >= policy.MaxAttempts)
                    throw new LlmProviderException(instanceName, null, null,
                        $"network error after {attempt} attempt(s): {ex.Message}");
                await DelayAsync(policy, attempt, retryAfter: null, ct);
                continue;
            }

            int status = (int)resp.StatusCode;
            if (status is >= 200 and < 300)
                return resp;

            bool retryable = status == 429 || status >= 500;
            if (!retryable || attempt >= policy.MaxAttempts)
            {
                var body = await SafeReadBody(resp, ct);
                resp.Dispose();
                throw new LlmProviderException(instanceName, status, body,
                    $"provider '{instanceName}' returned {status} after {attempt} attempt(s).");
            }

            var retryAfter = resp.Headers.RetryAfter?.Delta
                ?? (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null);
            resp.Dispose();
            await DelayAsync(policy, attempt, retryAfter, ct);
        }
        // Unreachable (loop always returns or throws), but the compiler needs a terminal.
        throw new LlmProviderException(instanceName, null, null,
            lastNetworkError?.Message ?? "retry loop exhausted");
    }

    private static Task DelayAsync(RetryPolicy policy, int attempt, TimeSpan? retryAfter, CancellationToken ct)
    {
        if (policy.BaseDelay == TimeSpan.Zero && retryAfter is null) return Task.CompletedTask;
        var backoff = TimeSpan.FromTicks(policy.BaseDelay.Ticks * (1L << (attempt - 1)));
        var delay = retryAfter is { } ra && ra > backoff ? ra : backoff;
        if (delay <= TimeSpan.Zero) return Task.CompletedTask;
        return Task.Delay(delay, ct);
    }

    private static async Task<string?> SafeReadBody(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); } catch { return null; }
    }
}
