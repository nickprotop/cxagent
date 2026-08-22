using System.Runtime.CompilerServices;

// THE TESTS ARE INSIDE. Session's wiring members — the Note*/Replace*/Take* pairs SessionFactory and
// SessionManager use to assemble a session — are internal, because a consumer calling one puts a
// session into a state nothing else expects. Four test files legitimately want that inside: they are
// tests OF the wiring rather than of the session's behaviour.
//
// AFTER A PACKAGE SPLIT this attribute travels with Core, and a consuming app's own tests will not
// see these members. That is correct: the wiring is Core's, and the tests that exercise it belong in
// Core's test project rather than an app's.
[assembly: InternalsVisibleTo("cxagent.Tests")]
