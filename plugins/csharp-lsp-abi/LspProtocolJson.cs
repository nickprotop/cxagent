using System.Text.Json.Serialization;

namespace CxAgent.Plugins.LspAbi;

// ---- request/notification param shapes -------------------------------------------------------
//
// NAMED RECORDS, WHERE THE MANAGED PLUGIN USES ANONYMOUS OBJECTS. This is the one shape difference
// JsonRpcConnection.cs's own doc names: NativeAOT's default JSON serializer refuses to serialize a
// type it cannot see ahead of time (an anonymous type has no name a source generator can be pointed
// at — see LspClient.cs, InitializeAsync's own comment for the exact failure this replaces), so
// every payload sent over the wire needs a real type, registered below.

public sealed record ClientCapabilities(
    TextDocumentClientCapabilities textDocument);

public sealed record TextDocumentClientCapabilities(
    DynamicRegistrationCapability synchronization,
    DynamicRegistrationCapability definition,
    DynamicRegistrationCapability references,
    PublishDiagnosticsCapability publishDiagnostics);

public sealed record DynamicRegistrationCapability(bool dynamicRegistration);

public sealed record PublishDiagnosticsCapability(bool relatedInformation);

public sealed record InitializeParams(
    int processId,
    string rootUri,
    ClientCapabilities capabilities);

public sealed record TextDocumentIdentifier(string uri);

public sealed record TextDocumentItem(string uri, string languageId, int version, string text);

public sealed record DidOpenTextDocumentParams(TextDocumentItem textDocument);

public sealed record LspPositionWire(int line, int character);

public sealed record TextDocumentPositionParams(
    TextDocumentIdentifier textDocument, LspPositionWire position);

public sealed record ReferenceContext(bool includeDeclaration);

public sealed record ReferenceParams(
    TextDocumentIdentifier textDocument, LspPositionWire position, ReferenceContext context);

/// <summary>
/// The source-generated resolver every <see cref="JsonRpcConnection"/> call in this plugin uses —
/// see that class's own doc for why this exists at all under NativeAOT. One context covering every
/// params shape this plugin ever sends, the native-plugin equivalent of the managed plugin needing
/// no such registration because it never leaves the JIT.
/// </summary>
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(ReferenceParams))]
internal partial class LspProtocolJson : JsonSerializerContext
{
}
