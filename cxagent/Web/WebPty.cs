using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace CxAgent.Web;

/// <summary>
/// A shell on a pseudo-terminal, for a browser tab: its bytes out, keystrokes in, and its size — on Linux an openpty
/// pair, on Windows a ConPTY, as the terminal's own /shell has.
///
/// <para>THE TERMINAL'S SHELL: <see cref="Core.Commands.ShellCommandLine.For"/> picks it — SHELL or ComSpec, with a
/// fallback that always exists — so a browser's shell is the one the terminal's tab opens.</para>
///
/// <para>KEYSTROKES ARE QUEUED, NOT WRITTEN WHERE THEY ARRIVE. A write to a terminal blocks once the shell's input is
/// full — a build that never reads stdin, and a large paste — and the caller is the browser connection's one message
/// loop: blocked there, it stops answering every prompt of every session in that tab. A writer of its own blocks
/// instead, and only this shell waits.</para>
/// </summary>
internal abstract class WebPty : IDisposable
{
    private readonly BlockingCollection<byte[]> _typed = new(boundedCapacity: 4096);
    private int _disposed;

    /// <summary>Starts the user's shell in <paramref name="folder"/> on a terminal of the given size.</summary>
    /// <exception cref="PlatformNotSupportedException">Neither Linux nor Windows.</exception>
    public static WebPty Start(string folder, int rows, int cols)
    {
        var child = Core.Commands.ShellCommandLine.For("");
        WebPty pty = OperatingSystem.IsLinux() ? new LinuxWebPty(child.Exe, folder, rows, cols)
            : OperatingSystem.IsWindows() ? new WindowsWebPty(child.Exe, folder, rows, cols)
            : throw new PlatformNotSupportedException("A shell in the browser needs Linux or Windows.");
        pty.StartWriter();
        return pty;
    }

    /// <summary>The shell's file name, for the tab's title.</summary>
    public abstract string ShellName { get; }

    private void StartWriter() =>
        new Thread(() =>
        {
            try
            {
                foreach (var bytes in _typed.GetConsumingEnumerable()) WriteAll(bytes);
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "web shell writer" }.Start();

    /// <summary>What was typed: queued for the writer, never blocking the caller. A full queue drops the keystroke.</summary>
    public void Write(byte[] data)
    {
        if (data.Length == 0 || Volatile.Read(ref _disposed) != 0) return;
        try { _typed.TryAdd(data); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Writes every byte, however many writes the terminal takes.</summary>
    protected abstract void WriteAll(byte[] data);

    /// <summary>The next bytes the shell wrote, blocking; 0 when it has gone.</summary>
    public abstract int Read(byte[] buffer);

    /// <summary>The browser's terminal changed size: the shell is told, and redraws.</summary>
    public abstract void Resize(int rows, int cols);

    /// <summary>The shell's exit code once it has exited.</summary>
    public abstract int? ExitCode { get; }

    /// <summary>Waits briefly for the shell to be gone, so its exit code can be read after the terminal closes.</summary>
    public abstract void WaitForExit(int ms);

    /// <summary>Ends the shell and everything it started.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _typed.CompleteAdding();
        Close();
    }

    /// <summary>The platform's teardown.</summary>
    protected abstract void Close();
}

/// <summary>
/// Linux: an openpty pair, the child being this executable started with <c>--pty-shim</c>, which
/// <see cref="SharpConsoleUI.PtyShim"/> turns into the shell with the PTY as its controlling terminal — the path the
/// terminal's /shell takes. Only the parent side is here, because the library keeps its own internal: a browser needs
/// the raw bytes for its terminal emulator, not the library's screen.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxWebPty : WebPty
{
    private int _master;
    private readonly Process _shim;
    private readonly string _shell;

    public LinuxWebPty(string shell, string folder, int rows, int cols)
    {
        _shell = shell;
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
        catch
        {
            // NOTHING LEFT OPEN by a shell that never started.
            close(master);
            _master = -1;
            throw;
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

    public override string ShellName => Path.GetFileName(_shell);

    public override int? ExitCode
    {
        get
        {
            try { return _shim.HasExited ? _shim.ExitCode : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public override void WaitForExit(int ms)
    {
        try { _shim.WaitForExit(ms); } catch (Exception) { }
    }

    public override int Read(byte[] buffer)
    {
        var fd = Volatile.Read(ref _master);
        return fd < 0 ? 0 : Math.Max(0, read(fd, buffer, buffer.Length));
    }

    // A WRITE MAY TAKE PART OF WHAT IT WAS GIVEN; the rest goes in the next one.
    protected override void WriteAll(byte[] data)
    {
        var done = 0;
        while (done < data.Length)
        {
            var fd = Volatile.Read(ref _master);
            if (fd < 0) return;
            var n = done == 0 ? write(fd, data, data.Length) : write(fd, data[done..], data.Length - done);
            if (n <= 0) return;
            done += n;
        }
    }

    public override void Resize(int rows, int cols)
    {
        var fd = Volatile.Read(ref _master);
        if (fd < 0 || rows < 1 || cols < 1) return;
        var size = new Winsize { Rows = (ushort)Math.Min(rows, 1000), Cols = (ushort)Math.Min(cols, 1000) };
        ioctl(fd, TiocSwinsz, ref size);
    }

    /// <summary>
    /// A hang-up to the shell's process group, then a kill for what ignored it. Closing the terminal alone leaves a
    /// build the shell started running.
    /// </summary>
    protected override void Close()
    {
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

/// <summary>
/// Windows: a ConPTY, built as the terminal's own Windows backend builds one — two pipes, a pseudo-console between
/// them, and the shell started attached to it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWebPty : WebPty
{
    private IntPtr _console;
    private IntPtr _process;
    private Stream? _input;
    private Stream? _output;
    private readonly string _shell;
    private int? _exitCode;

    public WindowsWebPty(string shell, string folder, int rows, int cols)
    {
        _shell = shell;
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe failed: {Marshal.GetLastWin32Error()}");
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
        {
            inRead.Dispose();
            inWrite.Dispose();
            throw new InvalidOperationException($"CreatePipe failed: {Marshal.GetLastWin32Error()}");
        }
        var hr = CreatePseudoConsole(new Coord { X = (short)cols, Y = (short)rows }, inRead, outWrite, 0, out _console);
        // THE CONSOLE HOLDS ITS OWN ENDS NOW; this side keeps only the ones it writes to and reads from.
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0)
        {
            inWrite.Dispose();
            outRead.Dispose();
            throw new InvalidOperationException($"CreatePseudoConsole failed: HRESULT 0x{hr:X8}");
        }
        _input = new FileStream(inWrite, FileAccess.Write, bufferSize: 4096);
        _output = new FileStream(outRead, FileAccess.Read, bufferSize: 4096);

        var startup = new StartupInfoEx();
        startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        nint listSize = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        var list = Marshal.AllocHGlobal(listSize);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref listSize)
                || !UpdateProcThreadAttribute(list, 0, ProcThreadAttributePseudoConsole, _console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException($"The shell's attributes could not be set: {Marshal.GetLastWin32Error()}");
            startup.lpAttributeList = list;
            if (!CreateProcess(null, Quote(shell), IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent,
                    IntPtr.Zero, folder, ref startup, out var info))
                throw new InvalidOperationException($"The shell could not start: {Marshal.GetLastWin32Error()}");
            _process = info.hProcess;
            CloseHandle(info.hThread);
        }
        catch
        {
            Close();
            throw;
        }
        finally
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }
    }

    private static string Quote(string exe) => exe.Contains(' ', StringComparison.Ordinal) ? $"\"{exe}\"" : exe;

    public override string ShellName => Path.GetFileName(_shell);

    public override int? ExitCode
    {
        get
        {
            if (_exitCode is { } done) return done;
            var process = _process;
            if (process == IntPtr.Zero || !GetExitCodeProcess(process, out var code) || code == StillActive) return null;
            return _exitCode = unchecked((int)code);
        }
    }

    public override void WaitForExit(int ms)
    {
        if (_process != IntPtr.Zero) WaitForSingleObject(_process, (uint)ms);
    }

    public override int Read(byte[] buffer)
    {
        try { return _output?.Read(buffer, 0, buffer.Length) ?? 0; }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { return 0; }
    }

    protected override void WriteAll(byte[] data)
    {
        try
        {
            _input?.Write(data, 0, data.Length);
            _input?.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public override void Resize(int rows, int cols)
    {
        if (_console == IntPtr.Zero || rows < 1 || cols < 1) return;
        ResizePseudoConsole(_console, new Coord { X = (short)Math.Min(cols, 1000), Y = (short)Math.Min(rows, 1000) });
    }

    /// <summary>Closes the console, which ends the shell attached to it; then waits, and terminates one that stayed — as the
    /// terminal's own Windows backend does.</summary>
    protected override void Close()
    {
        try { _input?.Dispose(); } catch (Exception) { }
        _input = null;
        var console = Interlocked.Exchange(ref _console, IntPtr.Zero);
        if (console != IntPtr.Zero) ClosePseudoConsole(console);
        try { _output?.Dispose(); } catch (Exception) { }
        _output = null;
        var process = Interlocked.Exchange(ref _process, IntPtr.Zero);
        if (process == IntPtr.Zero) return;
        if (WaitForSingleObject(process, 300) != 0)
        {
            TerminateProcess(process, 1);
            WaitForSingleObject(process, 300);
        }
        if (GetExitCodeProcess(process, out var code) && code != StillActive) _exitCode = unchecked((int)code);
        CloseHandle(process);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint StillActive = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nint attribute, IntPtr value, nint size, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? folder, ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
}
