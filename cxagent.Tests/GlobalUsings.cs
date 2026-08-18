// THE TWO NAMESPACES EVERY TEST TOUCHES. A session and the agent that runs its turns are separate
// concerns — see CxAgent.Core.Agents — but a test almost always names types from both, and a
// per-file using for a split the test is not about is noise the compiler can carry instead.
global using CxAgent.Core.Agents;
global using CxAgent.Core.Sessions;
