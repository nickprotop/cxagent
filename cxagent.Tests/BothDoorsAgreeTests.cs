using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE SAME MISTAKE MUST GET THE SAME ANSWER whichever door config came through. A rule that is true
/// because of how Core builds a provider does not stop being true because the caller wrote it in
/// code — and an embedder who gets no error gets a provider that fails at first use instead, far
/// from the cause.
/// </summary>
public class BothDoorsAgreeTests
{
    /// <summary>An openai-compatible endpoint with no baseUrl has nowhere to send a request. The
    /// file door refuses it; so must the code door.</summary>
    [Fact]
    public void CodeRefusesAnOpenAiCompatibleModelWithNoBaseUrl()
    {
        var config = new AgentConfig
        {
            Models = { ["local"] = new ModelConfig(ProviderKind.OpenAiCompatible, "m") { ApiKey = "k" } },
        };

        var resolved = config.Resolve();

        Assert.Contains(resolved.Errors, e => e.Contains("baseUrl"));
    }

    /// <summary>Anthropic has one address, so the same omission is fine there — the rule is
    /// conditional on the kind, which is why it cannot live in the type.</summary>
    [Fact]
    public void CodeAcceptsAnthropicWithNoBaseUrl()
    {
        var config = new AgentConfig
        {
            Models = { ["cloud"] = new ModelConfig(ProviderKind.Anthropic, "m") { ApiKey = "k" } },
        };

        var resolved = config.Resolve();

        Assert.DoesNotContain(resolved.Errors, e => e.Contains("baseUrl"));
    }

    /// <summary>The message names the instance, because a config with several models needs to say
    /// which one is wrong.</summary>
    [Fact]
    public void TheRefusalNamesTheModel()
    {
        var config = new AgentConfig
        {
            Models = { ["local"] = new ModelConfig(ProviderKind.OpenAiCompatible, "m") { ApiKey = "k" } },
        };

        Assert.Contains(config.Resolve().Errors, e => e.Contains("local"));
    }
}
