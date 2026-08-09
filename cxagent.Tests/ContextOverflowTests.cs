using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Recognising the provider's own "your context is too big" refusal.
///
/// <para>THE SECOND FIRING MOMENT, copied from opencode. Its trigger is not only the predictive one
/// (occupancy against the window) — it also compacts REACTIVELY, when a call comes back refused for
/// length (<c>session/processor.ts</c>, the ContextOverflowError branch of <c>halt</c>). The
/// predictive check can be wrong in both directions: a window that is misconfigured, an endpoint
/// serving less than it advertises, or a provider that reports no usage at all. The refusal itself
/// cannot be wrong — it is the endpoint saying so.</para>
///
/// <para>Patterns taken from opencode's <c>packages/llm/src/provider-error.ts</c>, which collects
/// them across the vendors it supports. Rate limits are excluded there and here: "too many requests"
/// is a wait, not a compaction.</para>
/// </summary>
public class ContextOverflowTests
{
    [Theory]
    // Verbatim from opencode's pattern list — these are real vendor strings, not invented ones.
    [InlineData("prompt is too long")]
    [InlineData("This model's maximum context length is 8192 tokens")]
    [InlineData("Input is too long for requested model")]
    [InlineData("Please reduce the length of the messages")]
    [InlineData("context_length_exceeded")]
    [InlineData("request entity too large")]
    [InlineData("Input token count 300000 exceeds the maximum")]
    // llama.cpp's own wording — the endpoint this project actually runs against.
    [InlineData("the request exceeds the available context size")]
    public void IsContextOverflow_RecognisesAProviderRefusal(string message)
    {
        Assert.True(ContextOverflow.IsOverflow(message, httpStatus: null));
    }

    /// <summary>
    /// A RATE LIMIT IS NOT AN OVERFLOW. Compacting in response to one throws away history to solve a
    /// problem that waiting solves — and the message often mentions tokens, which is exactly why
    /// opencode carries an explicit exclusion list rather than matching on "token" alone.
    /// </summary>
    [Theory]
    [InlineData("Rate limit reached for gpt-4 in organization org-x on tokens per min")]
    [InlineData("Too many requests")]
    [InlineData("Throttling error: rate exceeded")]
    [InlineData("Service unavailable: try again")]
    public void IsContextOverflow_IgnoresARateLimit(string message)
    {
        Assert.False(ContextOverflow.IsOverflow(message, httpStatus: 429));
    }

    /// <summary>413 Payload Too Large is the status form of the same refusal.</summary>
    [Fact]
    public void IsContextOverflow_Recognises413_WhateverTheBodySays()
    {
        Assert.True(ContextOverflow.IsOverflow("", httpStatus: 413));
    }

    /// <summary>An ordinary failure must not be mistaken for one. Compacting on a 401 would destroy
    /// history to "fix" a missing API key.</summary>
    [Theory]
    [InlineData("auth failed", 401)]
    [InlineData("internal server error", 500)]
    [InlineData("connection refused", null)]
    public void IsContextOverflow_IsFalseForAnOrdinaryFailure(string message, int? status)
    {
        Assert.False(ContextOverflow.IsOverflow(message, status));
    }

    /// <summary>
    /// The vendor BODY is searched too, not only the exception message. A wire that surfaces
    /// <c>{"error":{"code":"context_length_exceeded"}}</c> with a bland message would otherwise slip
    /// past — and that is the shape opencode's parseAPICallError checks explicitly.
    /// </summary>
    [Fact]
    public void IsContextOverflow_ReadsTheVendorBody()
    {
        Assert.True(ContextOverflow.IsOverflow(
            "Bad Request", httpStatus: 400,
            vendorBody: """{"error":{"code":"context_length_exceeded","message":"..."}}"""));
    }
}
