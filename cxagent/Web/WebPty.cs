using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CxAgent.Web;

/// <summary>
/// A shell on a pseudo-terminal, for a browser tab: its bytes out, keystrokes in, and its size.
///
/// <para>THE TERMINAL'S OWN CHILD SIDE. The child is this executable started with <c>--pty-shim</c>, which
/// <see cref="SharpConsoleUI.PtyShim"/> turns into the shell with the PTY as its controlling terminal — the path the
/// terminal's /shell takes. Only the parent side is here, because the library keeps its own internal: a browser needs
/// the raw bytes for its terminal emulator, not the library's screen.</para>
///
/// <para>LINUX, AS THAT SHIM IS.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class WebPty : IDisposable
{
    private int _master;
    private readonly Process _shim;
    private int _disposed;

    /// <summary>Starts <paramref name="shell"/> in <paramref name="folder"/> on a terminal of the given size.</summary>
    public WebPty(string shell, string folder, int rows, int cols)
    {
        var size = new Winsize { Rows = (ushort)rows, Cols = (ushort)cols };
        if (openpty(out var master, out var slave, IntPtr.Zero, IntPtr.Zero, ref size) != 0)
            throw new InvalidOperationException($"openpty failed: errno {Marshal.GetLastPInvokeError()}");

        // THE CHILD KEEPS THE SLAVE AND NEVER SEES THE MASTER: a shell holding the master would keep the terminal open
        // after this side closed it.
        fcntl(master, FSetFd, FdCloexec);
        fcntl(slave, FSetFd, 0);
        _master = master;

        var psi = new ProcessStartInfo(ShimHost()) { UseShellExecute = false, WorkingDirectory = folder };
        psi.Environment["TERM"] = "xterm-256color";
        foreach (var a in new[] { "--pty-shim", slave.ToString(System.Globalization.CultureInfo.InvariantCulture), shell })
            psi.ArgumentList.Add(a);
        try
        {
            _shim = Process.Start(psi) ?? throw new InvalidOperationException("The shell could not start.");
        }
        finally
        {
            close(slave);
        }
    }

    /// <summary>
    /// The executable that runs the shim: this one when it is cxagent, else cxagent beside this assembly — under a test
    /// host the process is dotnet's, which knows no --pty-shim.
    /// </summary>
    private static string ShimHost()
    {
        var self = Environment.ProcessPath;
        if (self is not null && Path.GetFileNameWithoutExtension(self).Equals("cxagent", StringComparison.OrdinalIgnoreCase)) return self;
        var beside = Path.Combine(AppContext.BaseDirectory, "cxagent");
        return File.Exists(beside) ? beside : self ?? "/proc/self/exe";
    }

    /// <summary>The shell's exit code once it has exited.</summary>
    public int? ExitCode
    {
        get
        {
            try { return _shim.HasExited ? _shim.ExitCode : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>Waits briefly for the shell to be gone, so its exit code can be read after the terminal closes.</summary>
    public void WaitForExit(int ms)
    {
        try { _shim.WaitForExit(ms); } catch (Exception) { }
    }

    /// <summary>The next bytes the shell wrote, blocking; 0 when it has gone.</summary>
    public int Read(byte[] buffer)
    {
        var fd = Volatile.Read(ref _master);
        return fd < 0 ? 0 : Math.Max(0, read(fd, buffer, buffer.Length));
    }

    /// <summary>What was typed.</summary>
    public void Write(byte[] data)
    {
        var fd = Volatile.Read(ref _master);
        if (fd >= 0 && data.Length > 0) write(fd, data, data.Length);
    }

    /// <summary>The browser's terminal changed size: the shell hears SIGWINCH and redraws.</summary>
    public void Resize(int rows, int cols)
    {
        var fd = Volatile.Read(ref _master);
        if (fd < 0 || rows < 1 || cols < 1) return;
        var size = new Winsize { Rows = (ushort)Math.Min(rows, 1000), Cols = (ushort)Math.Min(cols, 1000) };
        ioctl(fd, TiocSwinsz, ref size);
    }

    /// <summary>
    /// Ends the shell and everything it started: a hang-up to its process group, then a kill for what ignored it.
    /// Closing the terminal alone leaves a build the shell started running.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var pid = _shim.Id;
        try { kill(-pid, SigHup); } catch (Exception) { }
        try
        {
            if (!_shim.WaitForExit(300))
            {
                kill(-pid, SigKill);
                _shim.WaitForExit(300);
            }
        }
        catch (Exception) { }
        var fd = Interlocked.Exchange(ref _master, -1);
        if (fd >= 0) close(fd);
        _shim.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort Rows, Cols, XPixel, YPixel;
    }

    private const int FSetFd = 2;
    private const int FdCloexec = 1;
    private const ulong TiocSwinsz = 0x5414;
    private const int SigHup = 1;
    private const int SigKill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int openpty(out int master, out int slave, IntPtr name, IntPtr termios, ref Winsize size);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref Winsize size);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, byte[] buffer, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buffer, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
