using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The session as an OBJECT rather than as six locals in a 1,400-line method.
///
/// <para>Every field it holds already existed, captured by the composition root's wiring closure. A
/// local is one slot, so a second session would have needed a second copy of that method — naming
/// the state is what makes a second one possible. These tests pin the behaviour that used to live in
/// comments beside those locals.</para>
/// </summary>
public class SessionTests
{
    private static Session New() => new("/tmp/somewhere");

    [Fact]
    public void TheWorkingDirectoryIsGiven_NotReadFromTheProcess()
    {
        // THE WHOLE POINT OF THE TYPE. Two sessions in one process cannot each have their own
        // folder by consulting Environment.CurrentDirectory.
        Assert.Equal("/tmp/somewhere", New().WorkingDirectory);
        Assert.NotEqual(Directory.GetCurrentDirectory(), New().WorkingDirectory);
    }

    [Fact]
    public void ASessionStartsWithNoHost()
    {
        var s = New();
        Assert.Null(s.Host);
        Assert.Null(s.Provider);
        Assert.Null(s.SpendLabel);
    }

    /// <summary>
    /// CONSUMED ONCE. A carried ledger is handed to the NEXT wire and must not survive it — a
    /// re-wire two provider changes later must start fresh rather than inherit a stale ledger.
    /// </summary>
    [Fact]
    public void ACarriedLedger_IsTakenExactlyOnce()
    {
        var s = New();
        var ledger = new TokenLedger();
        s.CarryLedger(ledger);

        Assert.Same(ledger, s.TakeCarriedLedger());
        Assert.Null(s.TakeCarriedLedger());
    }

    [Fact]
    public void WithNothingCarried_TakingGivesNull()
    {
        Assert.Null(New().TakeCarriedLedger());
    }

    /// <summary>
    /// CONSUMED ONCE, for the same reason: an F5 provider swap later in the session must not
    /// silently re-restore a context the user has already moved past.
    /// </summary>
    [Fact]
    public void APendingResume_IsTakenExactlyOnce()
    {
        var s = New();
        var snapshot = new SessionSnapshot("id", [], 0, 0, DateTimeOffset.UtcNow);
        s.PendResume(snapshot);

        Assert.Same(snapshot, s.TakePendingResume());
        Assert.Null(s.TakePendingResume());
    }

    /// <summary>
    /// instance:model, THE LABEL EVERY SPEND READOUT USES. Two entries can serve the same model with
    /// different endpoints and windows, so the model alone cannot say where the tokens went.
    /// </summary>
    [Fact]
    public void SpendLabel_NamesTheInstanceAndTheModel()
    {
        var s = New();
        s.ReplaceHost(null!, new MockLlmProvider("qwen3"), "local", PluginRegistry.CreateWithBuiltins());

        Assert.Equal("local:qwen3", s.SpendLabel);
    }

    /// <summary>With no instance named there is nothing to qualify with — the bare model is still
    /// better than nothing.</summary>
    [Fact]
    public void SpendLabel_FallsBackToTheBareModel()
    {
        var s = New();
        s.ReplaceHost(null!, new MockLlmProvider("qwen3"), null, PluginRegistry.CreateWithBuiltins());

        Assert.Equal("qwen3", s.SpendLabel);
    }

    /// <summary>
    /// A RE-WIRE MOVES THREE FACTS TOGETHER: host, provider and instance name. They were three
    /// separate assignments a re-wire had to remember; one of them going stale is how a status bar
    /// ends up naming a model the session no longer uses.
    /// </summary>
    [Fact]
    public void ReplacingTheHost_UpdatesTheProviderAndInstanceWithIt()
    {
        var s = New();
        s.ReplaceHost(null!, new MockLlmProvider("first"), "local", PluginRegistry.CreateWithBuiltins());
        s.ReplaceHost(null!, new MockLlmProvider("second"), "small", PluginRegistry.CreateWithBuiltins());

        Assert.Equal("small:second", s.SpendLabel);
        Assert.Equal("small", s.InstanceName);
    }

    /// <summary>The registry travels with the wiring, so an F7 rebinding dispatches through the NEW
    /// resolution rather than the bindings that existed at launch.</summary>
    /// <summary>
    /// PLUGINS ARRIVE WITH THE FIRST WIRE, not with the constructor.
    ///
    /// <para>The session is built BEFORE the permission gate, because the gate needs the session's
    /// root string — so a session that demanded a registry up front could not exist before the gate
    /// that the registry itself needs. Null until wired is what breaks that cycle.</para>
    /// </summary>
    [Fact]
    public void ReplacingTheHost_SuppliesThePluginRegistry()
    {
        var s = New();
        Assert.Null(s.Plugins);

        var rewired = PluginRegistry.CreateWithBuiltins();
        s.ReplaceHost(null!, new MockLlmProvider(), "local", rewired);

        Assert.Same(rewired, s.Plugins);
    }

    // ---- the /model handoff ----------------------------------------------------------------------

    /// <summary>
    /// WITH NO HOST THERE IS NOTHING TO CARRY, and nothing is armed. A switch before the first wire
    /// must not leave a half-armed session that the next wire would restore from.
    /// </summary>
    [Fact]
    public void CarryToNextWire_WithNoHost_ArmsNothing()
    {
        var s = New();

        Assert.False(s.CarryToNextWire());
        Assert.Null(s.TakePendingResume());
        Assert.Null(s.TakeCarriedLedger());
    }

    // ---- steering ---------------------------------------------------------------------------------

    // ONE MESSAGE, APPENDED TO. A burst of corrections is one thought completed, and the previous
    // list was only ever consumed by joining it — so nothing downstream could tell a list from a
    // string, and a string cannot be half-delivered.
    [Fact]
    public void Steer_AppendsToWhatIsAlreadyWaiting()
    {
        var s = New();

        s.Steer("fix the header");
        s.Steer("and the indentation");

        Assert.Equal("fix the header\nand the indentation", s.PendingSteer);
    }

    // TAKEN ONCE. The turn takes it at a tool barrier and the model gets it exactly once; a second
    // barrier in the same turn must not deliver it again.
    [Fact]
    public void TakePendingSteer_ClearsIt()
    {
        var s = New();
        s.Steer("look at the tests");

        Assert.Equal("look at the tests", s.TakePendingSteer());
        Assert.Null(s.TakePendingSteer());
        Assert.Null(s.PendingSteer);
    }

    // A STEER TYPED AFTER ONE WAS DELIVERED STARTS FRESH, rather than appending to text the model
    // has already seen. "One pending message" is the rule, not "one per turn".
    [Fact]
    public void Steer_AfterATakeStartsANewMessage()
    {
        var s = New();
        s.Steer("first");
        s.TakePendingSteer();

        s.Steer("second");

        Assert.Equal("second", s.PendingSteer);
    }

    [Fact]
    public void PendingSteer_IsNullWhenNothingWasTyped() => Assert.Null(New().PendingSteer);
}
