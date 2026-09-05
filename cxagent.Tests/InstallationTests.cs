using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a launch knows about the install it belongs to.
///
/// <para>WORTH PINNING BECAUSE IT IS UNOBSERVABLE IN PRACTICE. "First run" is true once per install
/// and destroyed by reading it, so a mistake reaches a user exactly when nobody is watching and
/// cannot be reproduced by running the app again.</para>
/// </summary>
public class InstallationTests
{
    private static UsageHistoryStore StoreInTemp(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "cxagent-install-" + Guid.NewGuid().ToString("N"));
        return new UsageHistoryStore(new AppPaths(dir));
    }

    [Fact]
    public void AnEmptyStoreIsAFirstRun()
    {
        var history = StoreInTemp(out var dir);
        try
        {
            var first = Installation.Read(history, "1.0.0");

            Assert.True(first.IsFirstRun);
            Assert.Null(first.UpgradedFrom);   // nothing to have upgraded FROM
            Assert.Equal(1, first.LaunchCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>THE ANSWER IS CONSUMED. Reading is what makes the next launch ordinary, so a second
    /// call must not still say "first" — the reason a launch reads this once.</summary>
    [Fact]
    public void TheSecondLaunchIsNotAFirstRun()
    {
        var history = StoreInTemp(out var dir);
        try
        {
            Installation.Read(history, "1.0.0");
            var second = Installation.Read(history, "1.0.0");

            Assert.False(second.IsFirstRun);
            Assert.Null(second.UpgradedFrom);
            Assert.Equal(2, second.LaunchCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ANewVersionReportsWhatItCameFrom()
    {
        var history = StoreInTemp(out var dir);
        try
        {
            Installation.Read(history, "1.0.0");
            var upgraded = Installation.Read(history, "1.1.0");

            Assert.Equal("1.0.0", upgraded.UpgradedFrom);
            Assert.False(upgraded.IsFirstRun);   // upgrading is not installing
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>ONCE PER VERSION, NOT EVERY LAUNCH AFTER ONE. Anything gated on this — a "what's
    /// new" — must fire on the first launch of the new version and no later.</summary>
    [Fact]
    public void TheUpgradeDoesNotRepeat()
    {
        var history = StoreInTemp(out var dir);
        try
        {
            Installation.Read(history, "1.0.0");
            Installation.Read(history, "1.1.0");
            var settled = Installation.Read(history, "1.1.0");

            Assert.Null(settled.UpgradedFrom);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A DOWNGRADE REPORTS TOO. The field says the build CHANGED, not that it advanced:
    /// someone rolling back needs "this is a different build" as much as someone moving forward, and
    /// ordering versions would need a parser this deliberately does not have.</summary>
    [Fact]
    public void ADowngradeAlsoReports()
    {
        var history = StoreInTemp(out var dir);
        try
        {
            Installation.Read(history, "1.1.0");
            var back = Installation.Read(history, "1.0.0");

            Assert.Equal("1.1.0", back.UpgradedFrom);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// A STORE WITH TELEMETRY HAS RUN BEFORE, whatever the version row says. This separates "never
    /// ran" from "could not read the version", and it is what stops a first-run experience
    /// reappearing for someone who has been here for months.
    /// </summary>
    [Fact]
    public void AStoreWithHistoryIsNotAFirstRun()
    {
        var history = StoreInTemp(out var dir);
        try
        {
            history.SaveSession(new SessionRecord(
                AgentId: "a", WorkingDir: dir, ModelId: "m", Mode: "fan-out",
                InputTokens: 1, OutputTokens: 1, SubAgentTokens: 0, Turns: 1,
                StartedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow));

            Assert.False(Installation.Read(history, "1.0.0").IsFirstRun);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>THE INSTALL DATE IS WRITTEN ONCE. A value that moved would answer "when did I start
    /// using this" with "just now" forever.</summary>
    [Fact]
    public void TheInstallDateDoesNotMove()
    {
        var history = StoreInTemp(out var dir);
        try
        {
            var first = Installation.Read(history, "1.0.0");
            var later = Installation.Read(history, "1.1.0");

            Assert.NotNull(first.FirstSeen);
            Assert.Equal(first.FirstSeen, later.FirstSeen);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Everything survives a new store over the same directory — what makes any of this
    /// work across actual process restarts.</summary>
    [Fact]
    public void ItPersistsAcrossStores()
    {
        var history = StoreInTemp(out var dir);
        try
        {
            var first = Installation.Read(history, "1.0.0");

            var second = Installation.Read(new UsageHistoryStore(new AppPaths(dir)), "1.0.0");

            Assert.False(second.IsFirstRun);
            Assert.Equal(2, second.LaunchCount);
            Assert.Equal(first.FirstSeen, second.FirstSeen);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The environment fields are describing the running process, so they are filled rather
    /// than left to a caller — a null here would be a fact nobody could recover later.</summary>
    [Fact]
    public void ItDescribesTheRunningProcess()
    {
        var history = StoreInTemp(out var dir);
        try
        {
            var installation = Installation.Read(history, "1.0.0");

            Assert.NotEmpty(installation.Path);
            Assert.NotEmpty(installation.Runtime);
            Assert.NotEmpty(installation.Os);
            Assert.NotEmpty(installation.Architecture);
            Assert.NotEmpty(installation.UiVersion);
            // ALWAYS REPORTED, even when it equals the app's — the release stamps both from one tag,
            // so a match is the healthy case and a mismatch is a development build worth seeing.
            Assert.NotEmpty(installation.CoreVersion);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

}
