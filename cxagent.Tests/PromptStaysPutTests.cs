using System.Reflection;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That a prompt is raised where it belongs and does not move the user.
///
/// <para>THE WINDOW ONCE SWITCHED TO THE ASKING TAB, and that was not a whim: the prompt swaps into
/// a composer, and while "the composer" meant the ACTIVE tab's, going to the asking tab was the only
/// way to make the question reachable at all. The cost was an interruption — a session running
/// unattended is the normal case tabs exist for, and being pulled out of what you are typing to
/// answer somebody else's question is worse than finding it a second later.</para>
///
/// <para>NOW THE PROMPT GOES TO ITS OWN TAB. The waiting bar names the session, the strip carries a
/// dot, and Go takes the user there when they choose to be taken.</para>
/// </summary>
public class PromptStaysPutTests
{
    private static string Repo()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        while (!Directory.Exists(Path.Combine(dir, "cxagent", "UI")))
        {
            var up = Directory.GetParent(dir)?.FullName;
            Assert.NotNull(up);
            dir = up!;
        }
        return dir;
    }

    private static string Window() =>
        File.ReadAllText(Path.Combine(Repo(), "cxagent", "UI", "MainWindow.cs"));

    /// <summary>
    /// RAISING A PROMPT DOES NOT CHANGE TABS.
    ///
    /// <para>An assignment to ActiveTabIndex inside ShowPermissionPrompt is the switch coming back.
    /// Read as source because the alternative is composing a whole window with two live sessions and
    /// a gate; what regressed here was a line, and a line is what this watches.</para>
    /// </summary>
    [Fact]
    public void ShowingAPromptDoesNotSwitchTabs()
    {
        var source = Window();
        var start = source.IndexOf("public void ShowPermissionPrompt(IWindowControl prompt, Action? deny)",
            StringComparison.Ordinal);
        Assert.True(start > 0, "ShowPermissionPrompt not found");

        var body = source[start..source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal)];

        Assert.DoesNotContain("Tabs.ActiveTabIndex =", body);
    }

    /// <summary>AND ANSWERING ONE DOES NOT MOVE THE USER EITHER — there is nowhere to send them
    /// back to, because they were never taken anywhere.</summary>
    [Fact]
    public void RestoringAComposerDoesNotSwitchTabs()
    {
        var source = Window();
        var start = source.IndexOf("public void RestoreComposer(IWindowControl prompt)",
            StringComparison.Ordinal);
        Assert.True(start > 0, "RestoreComposer not found");

        var body = source[start..source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal)];

        Assert.DoesNotContain("Tabs.ActiveTabIndex =", body);
    }

    /// <summary>
    /// ARROWING THE TAB STRIP KEEPS THE KEYBOARD, even past a tab with a question up.
    ///
    /// <para>The strip has to hold focus for the NEXT arrow. Taking it on arrival drops the user out
    /// of the strip after one step, so continuing to browse needs F6 again — and a waiting tab is
    /// not a special case: passing one on the way to another is still passing it. A programmatic tab
    /// change is the opposite case, and is what Go uses.</para>
    ///
    /// <para>The guard is one condition, and the bug was reintroducing it BELOW the prompt branch
    /// instead of above — so this pins the order, which is the thing that was wrong.</para>
    /// </summary>
    [Fact]
    public void ArrowingTheStripIsNotInterruptedByAWaitingTab()
    {
        var source = Window();
        var start = source.IndexOf("private void ShowActiveSession()", StringComparison.Ordinal);
        Assert.True(start > 0, "ShowActiveSession not found");

        var body = source[start..source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal)];

        var guard = body.IndexOf("Tabs.HasFocus", StringComparison.Ordinal);
        var promptBranch = body.IndexOf("ActivePrompt is", StringComparison.Ordinal);

        Assert.True(guard > 0, "the browsing guard is gone: arrowing will steal focus from the strip");
        Assert.True(promptBranch > 0, "the prompt branch is gone");
        Assert.True(guard < promptBranch,
            "the browsing guard must come BEFORE the prompt branch — below it, arrowing past a "
            + "waiting tab drops the user out of the strip and browsing needs F6 again");
    }

    /// <summary>
    /// AND THE FIELD THAT REMEMBERED WHERE TO SEND THEM BACK IS GONE.
    ///
    /// <para>A leftover would be dead state that a later reader would take as a live rule — and
    /// worse, something a future edit could start honouring again without anyone deciding to.</para>
    /// </summary>
    [Fact]
    public void NothingRemembersATabToReturnTo()
    {
        Assert.DoesNotContain("_tabBeforePrompt", Window());
    }
}
