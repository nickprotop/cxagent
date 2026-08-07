using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using CxAgent.Core.Models;

namespace CxAgent.Core.Orchestrator;

/// <summary>
/// Polls a running <see cref="Process"/> on a timer and accumulates a bounded history of
/// CPU/memory snapshots. CPU percent is derived from the delta in cumulative
/// <see cref="Process.TotalProcessorTime"/> across the poll interval, normalized by
/// <see cref="Environment.ProcessorCount"/> so a fully-busy multi-core process reads ~100%
/// rather than (cores * 100)%. The first poll has no prior sample to diff against and is
/// skipped for CPU purposes (reported as 0%).
/// </summary>
public sealed class ProcessResourceMonitor : IDisposable
{
    private readonly Process _process;
    private readonly int _maxHistory;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private readonly List<ResourceSnapshot> _history = new();

    private TimeSpan? _lastCpuTime;
    private DateTimeOffset? _lastSampleTime;
    private int _disposed;

    public IReadOnlyList<ResourceSnapshot> History
    {
        get { lock (_lock) { return _history.ToArray(); } }
    }

    public event EventHandler<ResourceSnapshot>? Updated;

    public ProcessResourceMonitor(Process process, TimeSpan? pollInterval = null, int maxHistory = 60)
    {
        _process = process;
        _maxHistory = maxHistory;
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    private void Poll()
    {
        try
        {
            if (_process.HasExited) return;

            _process.Refresh();

            var now = DateTimeOffset.UtcNow;
            var cpuTime = _process.TotalProcessorTime;
            var memoryBytes = _process.WorkingSet64;

            double cpuPercent = 0;
            if (_lastCpuTime is TimeSpan lastCpu && _lastSampleTime is DateTimeOffset lastTime)
            {
                var wallDelta = now - lastTime;
                if (wallDelta > TimeSpan.Zero)
                {
                    var cpuDelta = cpuTime - lastCpu;
                    cpuPercent = cpuDelta.TotalMilliseconds / (wallDelta.TotalMilliseconds * Environment.ProcessorCount) * 100.0;
                }
            }

            _lastCpuTime = cpuTime;
            _lastSampleTime = now;

            var snapshot = new ResourceSnapshot(cpuPercent, memoryBytes, now);

            lock (_lock)
            {
                _history.Add(snapshot);
                while (_history.Count > _maxHistory)
                    _history.RemoveAt(0);
            }

            Updated?.Invoke(this, snapshot);
        }
        catch (InvalidOperationException)
        {
            // Process exited between the HasExited check and property reads.
        }
        catch (Win32Exception)
        {
            // Process exited / became inaccessible mid-poll.
        }
        catch
        {
            // Never let an exception escape the Timer callback thread — it is unobserved
            // and can tear down the process.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
    }
}
