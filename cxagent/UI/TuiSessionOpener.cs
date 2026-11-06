using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Web;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// How a browser opens and closes sessions when the terminal is serving them with /web: as tabs, through the
/// terminal's own doors.
///
/// <para>NO SESSION WITHOUT A TAB. A session the browser started that the terminal could not see would be an agent
/// running in this process with nobody at the keyboard able to stop it.</para>
///
/// <para>ON THE UI THREAD, waited for. The hub calls from a socket's thread; tabs are only made and closed on the
/// window's own.</para>
/// </summary>
public sealed class TuiSessionOpener(ConsoleWindowSystem system, MainWindow main, SessionManager manager, Func<NewSessionCommand> newSession) : ISessionOpener
{
    /// <inheritdoc/>
    public bool UnloadsIdle => false;

    /// <inheritdoc/>
    public Session Create(string folder) =>
        OnUi(() => newSession().OpenFolder(folder)) ?? throw new InvalidOperationException("The terminal could not open that folder.");

    /// <inheritdoc/>
    public Session Resume(SessionSnapshot snapshot) =>
        OnUi(() => newSession().OpenFolder(snapshot.WorkingDir ?? throw new InvalidOperationException("That session has no folder."), snapshot))
        ?? throw new InvalidOperationException("The terminal could not open that session.");

    /// <inheritdoc/>
    public Task<bool> CloseAsync(Session session) =>
        // CLOSED MEANS GONE FROM THE MANAGER — the tab's close may have asked first, or refused a running turn.
        Task.FromResult(OnUi(() => main.CloseSessionTab(session)) && !manager.Sessions.Contains(session));

    private T OnUi<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        system.EnqueueOnUIThread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        }, "tuiSessionOpener");
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }
}
