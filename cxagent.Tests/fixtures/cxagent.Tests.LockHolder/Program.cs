using CxAgent.Core.Storage;

// args: <locksDir> <agentId>. Prints "held" once the lock is taken, then waits to be killed.
using var held = SessionLock.TryAcquire(args[0], args[1], LockHolder.Web);
Console.WriteLine(held is null ? "refused" : "held");
Console.Out.Flush();
Thread.Sleep(Timeout.Infinite);
