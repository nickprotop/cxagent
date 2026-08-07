using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins.Builtin;

/// <summary>Makes an HTTP request with optional retry-until-expected-status.</summary>
public class HttpJobPlugin : IJobPlugin
{
    private static readonly HttpClient Shared = new();
    private readonly HttpClient _client;

    public HttpJobPlugin(HttpClient? client = null) => _client = client ?? Shared;

    public string TypeName => "http";
    public string DisplayName => "HTTP Request";

    public JobSchema GetSchema() => new(TypeName, DisplayName, new[]
    {
        new JobParamSpec("url", "string", Required: true, "Request URL"),
        new JobParamSpec("method", "string", Required: false, "HTTP method (default GET)"),
        new JobParamSpec("headers", "object", Required: false, "Request headers"),
        new JobParamSpec("body", "string", Required: false, "Request body"),
        new JobParamSpec("expect_status", "integer", Required: false, "Expected status (default: any 2xx)"),
        new JobParamSpec("retry_interval_seconds", "number", Required: false, "Delay between retries"),
        new JobParamSpec("max_retries", "integer", Required: false, "Max retry attempts"),
    });

    public JobValidation Validate(JobParameters parameters)
    {
        var url = parameters.Get("url", "");
        return Uri.TryCreate(url, UriKind.Absolute, out _)
            ? JobValidation.Valid()
            : JobValidation.Invalid("'url' must be a valid absolute URL.");
    }

    public async Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct)
    {
        var url = parameters.Get<string>("url");
        var method = new HttpMethod(parameters.Get("method", "GET"));
        var expectStatus = parameters.Get<int?>("expect_status", null);
        var maxRetries = parameters.Get("max_retries", 0);
        var retryInterval = parameters.Get("retry_interval_seconds", 0.0);
        var headers = parameters.Get<Dictionary<string, string>?>("headers", null);
        var body = parameters.Get<string?>("body", null);
        var start = DateTimeOffset.UtcNow;

        int attempt = 0;
        while (true)
        {
            try
            {
                using var req = new HttpRequestMessage(method, url);
                if (body is not null) req.Content = new StringContent(body);
                if (headers is not null)
                    foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);

                using var resp = await _client.SendAsync(req, ct);
                var status = (int)resp.StatusCode;
                var respBody = await resp.Content.ReadAsStringAsync(ct);
                context.Log($"http {method} {url} -> {status}");

                bool ok = expectStatus is int es ? status == es : status is >= 200 and < 300;
                if (ok)
                    return new JobResult
                    {
                        Success = true, ExitCode = 0, Duration = DateTimeOffset.UtcNow - start,
                        Output = new Dictionary<string, object?> { ["status"] = status, ["body"] = respBody },
                    };

                if (attempt++ >= maxRetries)
                    return new JobResult
                    {
                        Success = false, ExitCode = status, Duration = DateTimeOffset.UtcNow - start,
                        ErrorMessage = $"unexpected status {status} (expected {(expectStatus?.ToString() ?? "2xx")}) after {attempt} attempt(s)",
                        Output = new Dictionary<string, object?> { ["status"] = status, ["body"] = respBody },
                    };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt++ >= maxRetries)
                    return new JobResult { Success = false, ExitCode = -1, ErrorMessage = ex.Message, Duration = DateTimeOffset.UtcNow - start };
            }

            if (retryInterval > 0) await Task.Delay(TimeSpan.FromSeconds(retryInterval), ct);
        }
    }
}
