using System.Drawing;
using CxAgent.Core.Permissions;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Events;
using Xunit;

namespace CxAgent.Tests;

public class PermissionPromptControlTests
{
    private static PermissionRequest ShellRequest(string command) =>
        new(PermissionKind.Shell, command, command);

    private static PermissionRequest FileWriteRequest(string path) =>
        new(PermissionKind.FileWrite, path, path);

    [Fact]
    public void TheHeading_StatesTheREALReasonWeAreAsking_NotAlwaysOutsideTheFolder()
    {
        // Found by the live drive: an IN-TREE `notes.txt` in an untrusted folder was announced as
        // "Write a file outside the working folder?" — simply false. A security prompt that
        // misstates WHY it is asking teaches the user to stop reading its text, which is the one
        // thing this whole feature depends on them doing.
        //
        // offerTrust is the discriminator, and it is exact: the gate sets it only for an
        // in-boundary file request in an untrusted scope (Task 4 wiring). No new plumbing.
        var inTreeUntrusted = new PermissionPromptControl(FileWriteRequest("notes.txt"), offerTrust: true);
        var outsideTheFolder = new PermissionPromptControl(FileWriteRequest("/tmp/elsewhere.txt"));

        var inTreeText = string.Join("\n", FindMarkupLines(inTreeUntrusted.BuildContent()));
        var outsideText = string.Join("\n", FindMarkupLines(outsideTheFolder.BuildContent()));

        Assert.DoesNotContain("outside the working folder", inTreeText);
        Assert.Contains("untrusted", inTreeText);

        // ...and the genuinely-outside case must keep saying so — the fix must not flip the lie
        // around and start calling every out-of-tree write an untrusted-folder write.
        Assert.Contains("outside the working folder", outsideText);
        Assert.DoesNotContain("untrusted", outsideText);
    }

    private static ConsoleWindowSystem Sys() =>
        new(new HeadlessConsoleDriver(80, 24),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));

    /// <summary>Builds the prompt's content ONCE and hosts that same tree in a live, shown window —
    /// buttons need a real window (FocusManager, hit-testing) to accept a simulated click, and a
    /// second BuildContent() call would produce a detached tree the click could never reach.</summary>
    private static IWindowControl HostInAWindow(PermissionPromptControl prompt)
    {
        var system = Sys();
        var content = prompt.BuildContent();
        new SharpConsoleUI.Builders.WindowBuilder(system)
            .WithTitle("test")
            .AddControl(content)
            .BuildAndShow();
        return content;
    }

    /// <summary>Finds every ButtonControl nested under <paramref name="content"/> and clicks the
    /// first whose (unescaped) label contains <paramref name="fragment"/>, via the public mouse
    /// path (ProcessMouseEvent) rather than the internal PerformClickForTest, which is not visible
    /// outside SharpConsoleUI.Tests.</summary>
    private static void ClickButtonContaining(IWindowControl content, string fragment)
    {
        var button = FindButtons(content)
            .FirstOrDefault(b => b.Text.Contains(fragment, StringComparison.Ordinal));
        Assert.True(button is not null, $"no button labelled with '{fragment}' was found");

        // ProcessMouseEvent's hit-test is relative to the control's own bounds INCLUDING margin
        // (ButtonControl.cs: btnLeft = Margin.Left), so (0,0) is inside the margin, not the
        // button face, and the click silently misses. Click just past the left/top margin instead.
        var pos = new Point(button!.Margin.Left, button.Margin.Top);
        var args = new MouseEventArgs(
            new List<MouseFlags> { MouseFlags.Button1Clicked }, pos, pos, pos);
        var handled = button.ProcessMouseEvent(args);
        Assert.True(handled, $"click on '{fragment}' was not handled by the button");
    }

    private static string RenderToString(IWindowControl content) =>
        string.Join('\n', FindButtons(content).Select(b => b.Text));

    /// <summary>
    /// Collects the MARKUP text of the panel (headings and the displayed request), as distinct
    /// from <see cref="FindButtons"/>'s labels. Needed to assert on what the user actually READS
    /// in the prompt body — truncation, escaping — rather than only on the button row.
    /// </summary>
    private static IEnumerable<string> FindMarkupLines(IWindowControl control)
    {
        if (control is MarkupControl markup)
            yield return markup.Text;

        if (control is ScrollablePanelControl panel)
        {
            foreach (var child in panel.Children)
                foreach (var found in FindMarkupLines(child))
                    yield return found;
        }
    }

    private static IEnumerable<ButtonControl> FindButtons(IWindowControl control)
    {
        if (control is ButtonControl btn)
            yield return btn;

        // Descend ANY container, not just ScrollablePanelControl. The buttons moved into a
        // ToolbarControl (a horizontal row at the bottom of the dialog), and a walker that knew one
        // container type reported "no button labelled X" for buttons that were plainly there —
        // testing the layout rather than the behaviour it was written for.
        if (control is IContainerControl container)
        {
            foreach (var child in container.GetChildren())
                foreach (var found in FindButtons(child))
                    yield return found;
        }
    }

    [Fact]
    public async Task ClickingAlways_ResolvesTheCompletion_WithAlways()
    {
        var prompt = new PermissionPromptControl(ShellRequest("git status"));
        var content = HostInAWindow(prompt);      // buttons need a live window to click
        ClickButtonContaining(content, "Always");
        Assert.Equal(PermissionChoice.Always, await prompt.Completion);
    }

    [Fact]
    public async Task ClickingAllowOnce_ResolvesTheCompletion_WithOnce()
    {
        var prompt = new PermissionPromptControl(ShellRequest("git status"));
        var content = HostInAWindow(prompt);
        ClickButtonContaining(content, "Allow once");
        Assert.Equal(PermissionChoice.Once, await prompt.Completion);
    }

    [Fact]
    public async Task ClickingDeny_ResolvesTheCompletion_WithDeny()
    {
        var prompt = new PermissionPromptControl(ShellRequest("git status"));
        var content = HostInAWindow(prompt);
        ClickButtonContaining(content, "Deny");
        Assert.Equal(PermissionChoice.Deny, await prompt.Completion);
    }

    [Fact]
    public async Task DoubleClick_IsANoOp_TheFirstClickWins()
    {
        var prompt = new PermissionPromptControl(ShellRequest("git status"));
        var content = HostInAWindow(prompt);
        ClickButtonContaining(content, "Allow once");
        ClickButtonContaining(content, "Deny");
        Assert.Equal(PermissionChoice.Once, await prompt.Completion);
    }

    [Fact]
    public void ACommandContainingBrackets_StillRendersItsButtons()
    {
        // Unescaped '[' renders a button as an EMPTY ROW (ChoiceStepContent.cs:54-64, found live
        // on the F8 provider list). A shell command with brackets is a WHEN, not an IF.
        var prompt = new PermissionPromptControl(ShellRequest("echo [test] && ls [a-z]*"));
        var rendered = RenderToString(prompt.BuildContent());
        Assert.Contains("Deny", rendered);
        Assert.Contains("Allow once", rendered);
    }

    [Fact]
    public void NullAlwaysRule_OmitsTheAlwaysButton()
    {
        // A shell job with a custom env can't be truthfully generalised (PermissionPolicy.ShellRequest);
        // AlwaysRule is null and no Always button may be offered.
        var request = new PermissionRequest(PermissionKind.Shell, "FOO=bar do-thing", null);
        var prompt = new PermissionPromptControl(request);

        var labels = FindButtons(prompt.BuildContent()).Select(b => b.Text).ToList();

        Assert.Contains(labels, l => l.Contains("Allow once"));
        Assert.Contains(labels, l => l.Contains("Deny"));
        Assert.DoesNotContain(labels, l => l.Contains("Always"));
    }

    [Fact]
    public void OfferTrust_AddsATrustFolderButton()
    {
        var request = new PermissionRequest(PermissionKind.FileWrite, "/some/path", "/some/path");
        var prompt = new PermissionPromptControl(request, offerTrust: true);

        var labels = FindButtons(prompt.BuildContent()).Select(b => b.Text).ToList();

        Assert.Contains(labels, l => l.Contains("Trust this folder"));
    }

    [Fact]
    public void WithoutOfferTrust_NoTrustFolderButton()
    {
        var request = new PermissionRequest(PermissionKind.FileWrite, "/some/path", "/some/path");
        var prompt = new PermissionPromptControl(request);

        var labels = FindButtons(prompt.BuildContent()).Select(b => b.Text).ToList();

        Assert.DoesNotContain(labels, l => l.Contains("Trust this folder"));
    }

    [Fact]
    public async Task ClickingTrustFolder_ResolvesTheCompletion_WithTrustFolder()
    {
        var request = new PermissionRequest(PermissionKind.FileWrite, "/some/path", "/some/path");
        var prompt = new PermissionPromptControl(request, offerTrust: true);
        var content = HostInAWindow(prompt);
        ClickButtonContaining(content, "Trust this folder");
        Assert.Equal(PermissionChoice.TrustFolder, await prompt.Completion);
    }

    [Fact]
    public void VeryLongDisplay_IsTruncatedWithAnElisionMarker()
    {
        var longCommand = new string('x', 3000);
        var request = new PermissionRequest(PermissionKind.Shell, longCommand, longCommand);
        var prompt = new PermissionPromptControl(request);

        // Truncation is SECURITY-RELEVANT, not cosmetic: a 3000-char command rendered in full
        // pushes the buttons off-screen, and a user who cannot see what they are approving is
        // not consenting to it. So assert the elision actually happened rather than inferring
        // it from a successful build — the previous version of this test passed even with
        // truncation removed entirely.
        var content = prompt.BuildContent();
        var text = string.Join("\n", FindMarkupLines(content));

        Assert.Contains("(truncated)", text);
        Assert.DoesNotContain(new string('x', 2500), text);   // the full 3000 never reaches the pane

        var labels = FindButtons(content).Select(b => b.Text).ToList();
        Assert.Contains(labels, l => l.Contains("Allow once"));

        // ...and the ELIDED display must not corrupt what gets STORED. The rule the "Always"
        // button would persist is the full command; truncating it would silently store a
        // DIFFERENT (shorter) rule than the one the user was shown and approved.
        Assert.Equal(longCommand, request.AlwaysRule);
        Assert.Equal(3000, request.AlwaysRule!.Length);
    }
}
