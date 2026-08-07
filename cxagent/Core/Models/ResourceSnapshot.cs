namespace CxAgent.Core.Models;

/// <summary>A single CPU/memory sample for a monitored process.</summary>
public record ResourceSnapshot(double CpuPercent, long MemoryBytes, DateTimeOffset Timestamp);
