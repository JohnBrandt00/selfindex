namespace CodeIndexer.Services.Enrichers;

public sealed class FileSizeEnricher : IMetadataEnricher
{
    public string Name => "FileSize";

    public Task<Dictionary<string, object>> EnrichAsync(string filePath, string content, CancellationToken ct = default)
    {
        var result = new Dictionary<string, object>();
        try
        {
            result["file_size_bytes"] = new FileInfo(filePath).Length;
        }
        catch { }
        return Task.FromResult(result);
    }
}
