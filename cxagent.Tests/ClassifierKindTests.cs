using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Every <see cref="PermissionKind"/> the classifier can be handed must get an instruction that
/// actually describes what it is looking at. The shipped instruction was written for one kind
/// (file-write) and silently reused for all of them — an MCP call or an injected tool call fed
/// that text gets a verdict reasoned about the wrong thing, because the model was told it is
/// reviewing a file write when it is not.
/// </summary>
public class ClassifierKindTests
{
    [Fact]
    public void AnMcpCallGetsAnMcpInstruction()
    {
        // The shipped instruction says "You review one file-write action". An MCP call fed to it gets an
        // incoherent verdict, because the model is answering a question about a different thing.
        var instruction = ActionClassifier.InstructionFor(PermissionKind.Mcp);

        Assert.Contains("MCP", instruction);
        Assert.DoesNotContain("file-write", instruction);
    }

    [Fact]
    public void EveryKindHasAnInstruction()
    {
        // A kind with no instruction would fall back to the file-write one silently.
        foreach (var kind in Enum.GetValues<PermissionKind>())
            Assert.False(string.IsNullOrWhiteSpace(ActionClassifier.InstructionFor(kind)));
    }

    /// <summary>
    /// GUARDS CONTROLLER RULING 7. ActionClassifier prompts from <c>request.What</c>, which for Http
    /// is <c>Subject ?? Display</c> — and Http's Subject is deliberately the BARE ORIGIN (see
    /// RuleSubject's comment on Http in PermissionPolicy.cs), so the method, full URL and body size
    /// never reach the classifier through What at all. This test asserts they reach it a different
    /// way, through Facts, and would fail if PermissionPolicy stopped populating
    /// PermissionRequest.Facts.Http — the exact regression Ruling 7 exists to catch: HTTP
    /// classification silently reasoning about nothing but "a request went to example.com".
    /// </summary>
    [Fact]
    public void HttpFactsCarryTheMethodUrlAndBodySizePastTheBareOriginSubject()
    {
        var dict = new Dictionary<string, object?>
        {
            ["url"] = "https://example.com/upload?id=42",
            ["method"] = "POST",
            ["body"] = new string('x', 2048),
        };
        var reqs = PermissionPolicy.RequestsFor("http", new JobParameters(dict));
        var request = Assert.Single(reqs);

        // What IS the bare origin, by design — the assertion the ruling turns on.
        Assert.Equal("https://example.com", request.What);

        // The facts the classifier actually reads carry what What does not.
        Assert.NotNull(request.Facts);
        Assert.NotNull(request.Facts!.Http);
        Assert.Equal("POST", request.Facts.Http!.Method);
        Assert.Equal("https://example.com/upload?id=42", request.Facts.Http.Url);
        Assert.Equal(2048, request.Facts.Http.BodySize);

        // And what the classifier's own prompt body would contain, once Render()'d.
        var rendered = request.Facts.Render();
        Assert.Contains("POST", rendered);
        Assert.Contains("https://example.com/upload?id=42", rendered);
        Assert.Contains("2048 bytes", rendered);
    }
}
