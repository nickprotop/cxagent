namespace CxAgent.Core.Llm;

/// <summary>
/// OPTIONAL provider capability: enumerate the models this endpoint offers.
///
/// Deliberately NOT part of ILlmProvider — the HAL stays minimal (chat + stream), and a driver that
/// cannot enumerate simply doesn't implement this. The wizard does `provider is IModelCatalog cat`
/// and shows a dropdown when it can, a free-text field when it can't.
///
/// Contract: NEVER throws. A network/parse failure returns an empty list, because failing to list
/// models must degrade the wizard to typing a model id, not abort provider setup.
/// </summary>
public interface IModelCatalog
{
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}
