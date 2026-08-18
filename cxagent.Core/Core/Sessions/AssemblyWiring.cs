using System.Runtime.CompilerServices;

// THE TESTS ARE INSIDE. Session's wiring members — the Note*/Replace*/Take* pairs SessionFactory and
// SessionManager use to assemble a session — are internal, because a consumer calling one puts a
// session into a state nothing else expects. Four test files legitimately want that inside: they are
// tests OF the wiring rather than of the session's behaviour.
//
// THIS IS THE FIRST SUCH GRANT IN THIS CODEBASE, and it supersedes the note AppBootstrap and
// ChatTranscriptSink both carry ("public rather than internal: this codebase has no
// InternalsVisibleTo grant"). That was a statement about the status quo, not a principle — those two
// types are public because no grant existed to make them otherwise.
//
// AFTER A PACKAGE SPLIT this attribute travels with Core, and a consuming app's own tests will not
// see these members. That is correct: the wiring is Core's, and the tests that exercise it belong in
// Core's test project rather than an app's.
[assembly: InternalsVisibleTo("cxagent.Tests")]
