using System.Globalization;
using System.Runtime.InteropServices;
using CxAgent.Core.Plugins.Abi;

/// <summary>
/// The callback table a native plugin reports through — <c>cxagent_host</c> in cxagent_plugin.h.
///
/// <para>EACH REPORT BECOMES ONE EVENT LINE, keyed by the request it belongs to, for the app to route to
/// that call: the library runs in this process, and the protocol on stdout is the only way anything it
/// says reaches the app. A report this process cannot attribute is written with id 0, which the app
/// treats as the plugin's own log for a log line and drops otherwise.</para>
/// </summary>
internal static unsafe class HostCallbacks
{
    // THE LAYOUT cxagent_plugin.h DECLARES, field for field. Later fields are only ever appended, and
    // Size tells a library which of them this host has.
    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public uint Size;
        public delegate* unmanaged<byte*, double, byte*, void> Progress;
        public delegate* unmanaged<byte*, int, byte*, void> Log;
        public delegate* unmanaged<byte*, byte*, void> Stream;
        public delegate* unmanaged<byte*> CurrentCall;
    }

    // THE CALL RUNNING ON THIS THREAD — set by Running() around the library's invoke, so a report
    // made from inside invoke needs no id. Kept as UTF-8 too, for current_call to hand out.
    [ThreadStatic] private static long _current;
    [ThreadStatic] private static IntPtr _currentUtf8;

    private static Action<HostEvent>? _write;

    /// <summary>
    /// The table to pass to cxagent_plugin_attach. ALLOCATED ONCE AND NEVER FREED: the header promises
    /// it stays valid until the library is unloaded, and this process ends with the library.
    /// </summary>
    public static IntPtr Create(Action<HostEvent> write)
    {
        _write = write;
        var table = (Table*)NativeMemory.AllocZeroed((nuint)sizeof(Table));
        table->Size = (uint)sizeof(Table);
        table->Progress = &OnProgress;
        table->Log = &OnLog;
        table->Stream = &OnStream;
        table->CurrentCall = &OnCurrentCall;
        return (IntPtr)table;
    }

    /// <summary>Marks <paramref name="requestId"/> as this thread's call until the result is disposed.</summary>
    public static IDisposable Running(long requestId)
    {
        _current = requestId;
        _currentUtf8 = Marshal.StringToCoTaskMemUTF8(requestId.ToString(CultureInfo.InvariantCulture));
        return new Finished();
    }

    private sealed class Finished : IDisposable
    {
        public void Dispose()
        {
            Marshal.FreeCoTaskMem(_currentUtf8);
            _currentUtf8 = IntPtr.Zero;
            _current = 0;
        }
    }

    // A NULL id is this thread's call; anything else is the decimal request id current_call handed out.
    private static long CallOf(byte* id) =>
        id == null ? _current
        : long.TryParse(Marshal.PtrToStringUTF8((IntPtr)id), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static string? Text(byte* utf8) => utf8 == null ? null : Marshal.PtrToStringUTF8((IntPtr)utf8);

    // NOTHING MAY THROW BACK INTO NATIVE CODE — an exception crossing an [UnmanagedCallersOnly] frame
    // ends the process. A report that cannot be written is dropped.
    private static void Write(HostEvent report)
    {
        try { _write?.Invoke(report); }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly]
    private static void OnProgress(byte* id, double percent, byte* message) =>
        Write(new HostEvent("progress", CallOf(id), Percent: percent, Text: Text(message)));

    [UnmanagedCallersOnly]
    private static void OnLog(byte* id, int level, byte* line) =>
        Write(new HostEvent("log", CallOf(id), Level: level, Text: Text(line)));

    [UnmanagedCallersOnly]
    private static void OnStream(byte* id, byte* text) =>
        Write(new HostEvent("stream", CallOf(id), Text: Text(text)));

    [UnmanagedCallersOnly]
    private static byte* OnCurrentCall() => (byte*)_currentUtf8;
}
