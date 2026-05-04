namespace CodeIndexer.Services.Enrichers;

/// <summary>
/// Adds arbitrary key/value metadata to a chunk before it is stored in Qdrant.
/// Implement this interface and register with DI to add custom enrichers.
/// </summary>
public interface IMetadataEnricher
{
    string Name { get; }

    /// <summary>
    /// Return metadata pairs for the given file. Called once per file (not per chunk).
    /// Return an empty dict to add nothing.
    /// </summary>
    Task<Dictionary<string, object>> EnrichAsync(string filePath, string content, CancellationToken ct = default);
}
