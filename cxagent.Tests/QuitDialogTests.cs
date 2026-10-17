using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// "Quit cxagent?" — what Ctrl+Q asks when nothing is running. ON THE HEADLESS DRIVER, THROUGH THE REAL
/// DIALOG and real keys: which answer Enter reaches, and whether Space reaches the box, are decided by the
/// dialog's own focus and selection.
/// </summary>
public class QuitDialogTests : IDisposable
{
    // THE DIALOG IS STATIC, so a test that fails with it open would leave the next one's Open answering
    // Cancel at once — a second, misleading failure.
    public void Dispose() => QuitDialog.CloseIfOpen();

    private static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0') => new(c, key, false, false, false);

    private static Task<QuitChoice> Open()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(100, 30));
        CxAgentTheme.Install(system, null);
        return QuitDialog.AskAsync(system);
    }

    /// <summary>QUIT FIRST: nothing is running, so the reflex Enter after Ctrl+Q should quit.</summary>
    [Fact]
    public void ItOpensOnQuit_WithTheBoxUnticked()
    {
        var asked = Open();
        try
        {
            Assert.True(QuitDialog.IsOpen);
            Assert.Equal("Quit", QuitDialog.Highlighted);
            Assert.False(QuitDialog.StopAskingTicked);
            Assert.True(QuitDialog.AnswersFocused);
        }
        finally
        {
            QuitDialog.CloseIfOpen();
        }
        Assert.Equal(QuitChoice.Cancel, asked.GetAwaiter().GetResult());
    }

    [Fact]
    public void Enter_Quits()
    {
        var asked = Open();
        QuitDialog.PressForTest(Key(ConsoleKey.Enter, '\r'));

        Assert.True(asked.IsCompleted, "Enter did not answer");
        Assert.Equal(QuitChoice.Quit, asked.Result);
        Assert.False(QuitDialog.IsOpen);
    }

    [Fact]
    public void TabThenSpace_TicksTheBox_AndQuittingSaysToStopAsking()
    {
        var asked = Open();
        QuitDialog.PressForTest(Key(ConsoleKey.Tab, '\t'));
        QuitDialog.PressForTest(Key(ConsoleKey.Spacebar, ' '));
        Assert.True(QuitDialog.StopAskingTicked);

        // Back to the answers, which still open on Quit.
        QuitDialog.PressForTest(new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: true, false, false));
        QuitDialog.PressForTest(Key(ConsoleKey.Enter, '\r'));

        Assert.True(asked.IsCompleted, "Enter did not answer");
        Assert.Equal(QuitChoice.QuitAndStopAsking, asked.Result);
    }

    /// <summary>A TICK THEN CANCEL SAVES NOTHING: the box takes effect only with the quit it rode in on.</summary>
    [Fact]
    public void ATickThenCancel_IsJustCancel()
    {
        var asked = Open();
        QuitDialog.PressForTest(Key(ConsoleKey.Tab, '\t'));
        QuitDialog.PressForTest(Key(ConsoleKey.Spacebar, ' '));
        QuitDialog.CloseIfOpen();

        Assert.Equal(QuitChoice.Cancel, asked.GetAwaiter().GetResult());
    }

    [Fact]
    public void ChoosingCancel_Cancels()
    {
        var asked = Open();
        QuitDialog.PressForTest(Key(ConsoleKey.DownArrow));
        QuitDialog.PressForTest(Key(ConsoleKey.Enter, '\r'));

        Assert.True(asked.IsCompleted, "Enter did not answer");
        Assert.Equal(QuitChoice.Cancel, asked.Result);
    }

    /// <summary>A SECOND CTRL+Q WHILE IT IS UP OPENS NOTHING MORE, and the first question stays.</summary>
    [Fact]
    public void AskingAgain_WhileOpen_AnswersCancelAtOnce()
    {
        var first = Open();
        try
        {
            var second = Open();
            Assert.True(second.IsCompleted);
            Assert.Equal(QuitChoice.Cancel, second.Result);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            QuitDialog.CloseIfOpen();
        }
    }
}
