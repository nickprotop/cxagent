using System.Reflection;
using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE BOUNDARY, ASSERTED RATHER THAN INTENDED. A session reports facts; presentation lives one layer
/// up. Both properties below held by convention before this test existed, which is exactly how a
/// boundary erodes — one reasonable-looking call at a time.
/// </summary>
public class SessionBoundaryTests
{
    /// <summary>
    /// THE SESSION'S PORT IS NOT A MESSAGE BUS. ShowSystemMessage had 26 call sites and Core called it
    /// none of them: the UI was printing to its own transcript through the session's observer for want
    /// of a writer of its own.
    /// </summary>
    [Fact]
    public void TheObserver_HasNoGeneralPurposeMessageMethod()
    {
        var names = typeof(ISessionObserver).GetMethods().Select(m => m.Name).ToList();

        Assert.DoesNotContain("ShowSystemMessage", names);
        Assert.DoesNotContain("Notice", names);
    }

    /// <summary>
    /// CORE DOES NOT REFERENCE THE UI. The one direction that must never reverse — the UI implements
    /// Core's interfaces, never the other way round.
    /// </summary>
    [Fact]
    public void Core_DoesNotDependOnTheUiNamespace()
    {
        var coreTypes = typeof(ISessionObserver).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("CxAgent.Core", StringComparison.Ordinal) == true);

        foreach (var type in coreTypes)
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                                                   | BindingFlags.Instance | BindingFlags.Static
                                                   | BindingFlags.DeclaredOnly))
            {
                var signature = member switch
                {
                    MethodInfo m => m.ReturnType.FullName + string.Concat(m.GetParameters().Select(p => p.ParameterType.FullName)),
                    PropertyInfo p => p.PropertyType.FullName,
                    FieldInfo f => f.FieldType.FullName,
                    _ => null,
                };

                Assert.DoesNotContain("CxAgent.UI", signature ?? "", StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// A PURE SINK. Once the session mints ids, nothing needs to flow back — and an observer that
    /// returns nothing is one you can have two of.
    /// </summary>
    [Fact]
    public void TheObserver_ReturnsNothingFromAnyMember()
    {
        foreach (var method in typeof(ISessionObserver).GetMethods())
            Assert.Equal(typeof(void), method.ReturnType);
    }

    /// <summary>
    /// TWO OBSERVERS SEE THE SAME TURN AS THE SAME TURN. This is the property that inverting id
    /// generation exists for: while each implementation minted its own, two observers on one session
    /// disagreed about which turn was which, so a second observer could not be added at all.
    /// </summary>
    [Fact]
    public async Task TwoObservers_ReceiveIdenticalTurnIds()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new Core.Llm.LlmResponse { Text = "hi", StopReason = "end_turn" });

        var first = new RecordingObserver();
        var second = new RecordingObserver();

        var agent = new Agent(provider, Core.Plugins.PluginRegistry.CreateWithBuiltins(),
            new Core.Llm.TokenLedger(), new FanOutObserver(first, second), new NullJobPanel(),
            logs: null, maxTurns: 5);

        await agent.SendAsync("go", CancellationToken.None);

        Assert.NotEmpty(first.AssistantTurns);
        Assert.Equal(first.AssistantTurns, second.AssistantTurns);
    }

    /// <summary>Records the ids it is told about — the minimum an observer can be.</summary>
    private sealed class RecordingObserver : ISessionObserver
    {
        public List<ChatMessageId> AssistantTurns { get; } = [];

        public void UserTurnAdded(ChatMessageId id, string text) { }
        public void AssistantTurnBegan(ChatMessageId id) => AssistantTurns.Add(id);
        public void AssistantTextAppended(ChatMessageId id, string token) { }
        public void AssistantReasoningAppended(ChatMessageId id, string text) { }
        public void AssistantTurnEnded(ChatMessageId id) { }
        public void AssistantLabelled(ChatMessageId id, string label) { }
        public void Failed(string message) { }
        public void Said(string message) { }
    }

    /// <summary>Hands every report to two observers — the thing that was impossible while each minted
    /// its own ids.</summary>
    private sealed class FanOutObserver(ISessionObserver a, ISessionObserver b) : ISessionObserver
    {
        public void UserTurnAdded(ChatMessageId id, string text)
        { a.UserTurnAdded(id, text); b.UserTurnAdded(id, text); }
        public void AssistantTurnBegan(ChatMessageId id)
        { a.AssistantTurnBegan(id); b.AssistantTurnBegan(id); }
        public void AssistantTextAppended(ChatMessageId id, string token)
        { a.AssistantTextAppended(id, token); b.AssistantTextAppended(id, token); }
        public void AssistantReasoningAppended(ChatMessageId id, string text)
        { a.AssistantReasoningAppended(id, text); b.AssistantReasoningAppended(id, text); }
        public void AssistantTurnEnded(ChatMessageId id)
        { a.AssistantTurnEnded(id); b.AssistantTurnEnded(id); }
        public void AssistantLabelled(ChatMessageId id, string label)
        { a.AssistantLabelled(id, label); b.AssistantLabelled(id, label); }
        public void Failed(string message) { a.Failed(message); b.Failed(message); }
        public void Said(string message) { }
    }
}
