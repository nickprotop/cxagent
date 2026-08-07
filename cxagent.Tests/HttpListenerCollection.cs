using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Serialises every test class that binds an <c>HttpListener</c>, against each other only.
///
/// <para>WHY: <c>HttpEndPointManager</c> is a PROCESS-GLOBAL map keyed by port. When two listeners
/// in different classes touch the same port concurrently, one of them later throws
/// <c>HttpListenerException: "Address already in use"</c> from <c>Close</c> ->
/// <c>RemoveListener</c> -> <c>RemovePrefixInternal</c> -> <c>GetEPListener</c> — during TEARDOWN,
/// in whichever class happened to dispose, at ~4ms, before any request completed. That is why this
/// presented for weeks as a "flaky LlmHttpRetryTests" that moved between classes every run and
/// resisted diagnosis: the failing test was never the one that caused it.</para>
///
/// <para>MEASURED, so nobody re-litigates it. Full-suite runs, failures out of total:
/// <list type="bullet">
///   <item>baseline: 3/8</item>
///   <item>+ awaiting the server loop on dispose (a real use-after-dispose, kept): 2/8</item>
///   <item>+ probing the port with a TcpListener before Start (kept): 2/10</item>
///   <item>xUnit.ParallelizeTestCollections=false, whole suite: 0/4 — but 8s instead of 3s</item>
/// </list>
/// This collection is that last result WITHOUT the cost: the seven listener classes run one at a
/// time, everything else stays parallel.</para>
///
/// <para>Two port-allocation fixes were tried BEFORE the cause was understood and are recorded in
/// the D9 task as measured failures — the ports were never the problem. Do not reach for a third.</para>
/// </summary>
[CollectionDefinition("http-listeners")]
public sealed class HttpListenerCollection
{
}
