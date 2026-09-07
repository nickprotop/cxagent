using System.Reflection;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That two tabs share no state.
///
/// <para><b>THIS IS THE CLASS PHASE ONE'S BUGS LIVED IN.</b> Every one of them had the same shape:
/// something the WINDOW held once, which was right with one conversation and wrong with two — a
/// composer, a transcript, a job sink, a prompt, a deny action, a question, a spend reading, a turn
/// tally, a resolution, a mode, a queued block, a skill list. They were found one at a time over
/// many rounds, each time after the previous round was believed to have exhausted them.</para>
///
/// <para>SO THE TEST IS WRITTEN BY REFLECTION RATHER THAN BY HAND. A hand-written test asserts the
/// fields somebody thought of; this one fails for a field added later that is accidentally shared,
/// which is exactly the failure that kept recurring. It is the check that would have caught them as
/// a class rather than as fifteen separate bug reports.</para>
/// </summary>
public class SessionTabIsolationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-tabiso-" + Guid.NewGuid().ToString("N"));

    private readonly LogFileManager _logs;
    private readonly ConsoleWindowSystem _system;

    public SessionTabIsolationTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();

        _logs = new LogFileManager(paths);
        _system = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24),
            new SharpConsoleUI.Configuration.ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A tab built the way the window builds one: its own controls, nothing shared.</summary>
    private SessionTab Tab() =>
        new(session: null, new ChatTranscriptControl(), new PromptControl(),
            new JobPanelControl(_system, _logs));

    /// <summary>
    /// EVERY REFERENCE MEMBER IS THIS TAB'S OWN — no two tabs point at one object.
    ///
    /// <para>Shared CONTROLS are the dangerous case: one composer between two tabs sends a line to
    /// whichever conversation happened to be in front, and one transcript renders both sessions'
    /// output into a single history.</para>
    /// </summary>
    [Fact]
    public void TwoTabsShareNoReferenceState()
    {
        var alpha = Tab();
        var beta = Tab();

        var shared = new List<string>();

        foreach (var member in typeof(SessionTab).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (member.PropertyType.IsValueType || member.PropertyType == typeof(string)) continue;
            if (member.GetIndexParameters().Length > 0) continue;

            var a = member.GetValue(alpha);
            var b = member.GetValue(beta);

            // NULL ON BOTH IS NOT SHARING — an unset reference is the common case for a tab that
            // has not been composed, and two nulls are not one object.
            if (a is null || b is null) continue;

            // NOR IS A SHARED EMPTY IMMUTABLE. `= []` on an IReadOnlyList compiles to
            // Array.Empty<T>(), which is a singleton by design: every empty list in the process is
            // that one object. Sharing something nobody can write to is not the failure this looks
            // for — the failure is two tabs writing through one reference.
            if (a is System.Collections.ICollection { Count: 0 }) continue;

            if (ReferenceEquals(a, b)) shared.Add(member.Name);
        }

        Assert.True(shared.Count == 0,
            "these members are the SAME object in two tabs: " + string.Join(", ", shared));
    }

    /// <summary>
    /// AND WRITING ONE TAB'S VALUE DOES NOT MOVE THE OTHER'S.
    ///
    /// <para>The reference check above cannot see a member backed by a STATIC field — two tabs would
    /// read the same value while looking independent. This writes through every settable property
    /// and reads the other tab back.</para>
    /// </summary>
    [Fact]
    public void WritingOneTabDoesNotChangeTheOther()
    {
        var alpha = Tab();
        var beta = Tab();

        var leaked = new List<string>();

        foreach (var member in typeof(SessionTab).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!member.CanWrite || member.SetMethod?.IsPublic != true) continue;
            if (member.GetIndexParameters().Length > 0) continue;
            if (Sample(member.PropertyType) is not { } value) continue;

            var before = member.GetValue(beta);
            member.SetValue(alpha, value);
            var after = member.GetValue(beta);

            if (!Equals(before, after)) leaked.Add(member.Name);
        }

        Assert.True(leaked.Count == 0,
            "writing these on one tab changed the other: " + string.Join(", ", leaked));
    }

    /// <summary>
    /// AND THE TALLY IS A REFERENCE THE PANEL CAN HOLD.
    ///
    /// <para>Its own test, because the panel keeps a REFERENCE to it and increments through that —
    /// so a tally that copied on assignment would send every recorded turn to a value nobody reads
    /// again. That is not a sharing bug but its opposite, and the reflection sweep above cannot
    /// express it.</para>
    /// </summary>
    [Fact]
    public void ATabsTallyIsItsOwnAndIsMutableThroughAReference()
    {
        var alpha = Tab();
        var beta = Tab();

        var held = alpha.Tally;
        held.Turns += 3;

        Assert.Equal(3, alpha.Tally.Turns);
        Assert.Equal(0, beta.Tally.Turns);
    }

    /// <summary>A value this property can hold, or null when the sweep should skip it.</summary>
    private static object? Sample(Type type) =>
        type == typeof(int) ? 7
        : type == typeof(int?) ? 7
        : type == typeof(bool) ? true
        : type == typeof(string) ? "written"
        : type == typeof(IReadOnlyList<string>) ? (object)new[] { "written" }
        : type == typeof(WorkingMode) ? WorkingMode.Default
        : null;   // controls and delegates: the reference sweep covers those
}
