namespace Fynydd.Umbraco.Search.Postgres.Services;

/// <summary>
/// Provides text replacements applied to semantic-search content immediately before chunking.
/// </summary>
public interface ITextReplacementProvider
{
    /// <summary>
    /// Gets the current find/replace mapping.
    /// </summary>
    IReadOnlyDictionary<string, string> GetReplacements();
}
