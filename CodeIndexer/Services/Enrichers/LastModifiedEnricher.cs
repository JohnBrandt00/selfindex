namespace CodeIndexer.Services.Enrichers;

public sealed class LastModifiedEnricher : IMetadataEnricher
{
    public string Name => "LastModified";

    public Task<Dictionary<string, object>> EnrichAsync(string filePath, string content, CancellationToken ct = default)
    {
        var result = new Dictionary<string, object>();
        try
        {
            var info = new FileInfo(filePath);
            result["last_modified"] = info.LastWriteTimeUtc.ToString("o");
            result["created"]       = info.CreationTimeUtc.ToString("o");
        }
        catch { }
        return Task.FromResult(result);
    }
}
