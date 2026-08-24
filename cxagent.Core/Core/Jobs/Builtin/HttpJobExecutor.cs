using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs.Builtin;

/// <summary>Makes an HTTP request with optional retry-until-expected-status.</summary>
public class HttpJobExecutor : IJobExecutor
{
    private static readonly HttpClient Shared = new();
    private readonly HttpClient _client;

    public HttpJobExecutor(HttpClient? client = null) => _client = client ?? Shared;

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
        new JobParamSpec("as_text", "boolean", Required: false,
            "Convert an HTML response to readable text. Non-HTML is returned unchanged."),
    });

    public JobValidation Validate(JobParameters parameters)
    {
        var url = parameters.Get("url", "");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return JobValidation.Invalid("'url' must be a valid absolute URL.");

        // SCHEME, not just well-formedness. `file:///etc/passwd` and `ftp://…` are absolute URIs, so
        // they passed here and then threw NotSupportedException out of SendAsync — an exception the
        // model reads as a transport failure rather than as "this tool does not do that".
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return JobValidation.Invalid(
                $"'url' must use http or https; '{uri.Scheme}' is not supported by this tool.");

        // A method the HttpMethod constructor rejects (whitespace, a stray quote) threw a raw
        // FormatException from ExecuteAsync naming neither the parameter nor the value.
        var method = parameters.Get("method", "GET");
        if (method.Length == 0 || method.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return JobValidation.Invalid(
                $"'method' must be a bare HTTP verb such as GET or POST; got '{method}'.");

        return JobValidation.Valid();
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
        var asText = parameters.Get("as_text", false);
        var start = DateTimeOffset.UtcNow;

        int attempt = 0;
        while (true)
        {
            try
            {
                using var req = new HttpRequestMessage(method, url);

                // CONTENT HEADERS GO ON THE CONTENT. req.Headers.TryAddWithoutValidation returns
                // false for Content-Type and silently DISCARDS it, so a POST always went out as
                // StringContent's default text/plain. That is a guaranteed retry loop: the API
                // rejects the body, the model correctly diagnoses it and retries with
                // headers:{"Content-Type":"application/json"}, and gets the identical request back.
                if (body is not null)
                {
                    var declared = headers?.FirstOrDefault(h =>
                        string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase));

                    // A body that parses as JSON is JSON. A model sending one to an API and being
                    // told text/plain has no way to see what went wrong from the response alone.
                    var contentType = declared?.Value is { Length: > 0 } ct2 ? ct2
                        : LooksLikeJson(body) ? "application/json"
                        : "text/plain";

                    req.Content = new StringContent(body, System.Text.Encoding.UTF8);
                    req.Content.Headers.ContentType =
                        System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
                }

                if (headers is not null)
                    foreach (var h in headers)
                    {
                        // Content-Type was applied above; adding it here would fail anyway.
                        if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value))
                            req.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }

                using var resp = await _client.SendAsync(req, ct);
                var status = (int)resp.StatusCode;
                var respBody = await resp.Content.ReadAsStringAsync(ct);

                // HTML TO TEXT, WHEN ASKED. A page is almost entirely markup, and a tool result is
                // re-sent on every later turn — ten raw fetches measured at 200k of context. The
                // conversion is opt-in because http_request is also how the model talks to APIs, and
                // rewriting a JSON body would corrupt exactly what the caller asked for.
                //
                // CONTENT TYPE DECIDES, not the flag alone: a server that answers a fetch with JSON
                // gets passed through even when as_text was set, because the caller wanted the
                // resource and this is what the resource is.
                var contentTypeHeader = resp.Content.Headers.ContentType?.ToString();
                if (asText && HtmlToText.IsHtml(contentTypeHeader))
                {
                    var before = respBody.Length;
                    respBody = HtmlToText.Convert(respBody);
                    context.Log($"html -> text: {before:N0} -> {respBody.Length:N0} chars");
                }

                context.Log($"http {method} {url} -> {status}");

                bool ok = expectStatus is int es ? status == es : status is >= 200 and < 300;
                if (ok)
                    return new JobResult
                    {
                        Success = true, ExitCode = 0, Duration = DateTimeOffset.UtcNow - start,
                        // content CARRIES THE BODY BECAUSE THAT IS WHAT THE MODEL READS. Agent
                        // renders a successful tool result from Output["summary"] or
                        // Output["content"] and nothing else, so a result offering neither arrives
                        // as an empty string — the model sees a call that succeeded and returned
                        // nothing, and explains the emptiness rather than reporting it. status and
                        // body stay beside it: they are the typed answer, and a caller reading
                        // JobResult directly wants the status without parsing it back out of text.
                        Output = new Dictionary<string, object?>
                        {
                            ["content"] = respBody, ["status"] = status, ["body"] = respBody,
                        },
                    };

                if (attempt++ >= maxRetries)
                    return new JobResult
                    {
                        Success = false, ExitCode = status, Duration = DateTimeOffset.UtcNow - start,
                        ErrorMessage = $"unexpected status {status} (expected {(expectStatus?.ToString() ?? "2xx")}) after {attempt} attempt(s)",
                        // ErrorMessage is what a FAILED result renders as, so content is not read
                        // here — it is set anyway so the shape does not depend on which branch built
                        // it, and a caller that inspects Output finds the body in the same key.
                        Output = new Dictionary<string, object?>
                        {
                            ["content"] = respBody, ["status"] = status, ["body"] = respBody,
                        },
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

    /// <summary>Whether a body is JSON, by its first non-space character. Deliberately shallow: a
    /// full parse would reject a body that is JSON-with-a-trailing-comma, which the server should
    /// get the chance to reject itself with a real error message.</summary>
    private static bool LooksLikeJson(string body)
    {
        var t = body.AsSpan().TrimStart();
        return t.Length > 0 && (t[0] == '{' || t[0] == '[');
    }
}
