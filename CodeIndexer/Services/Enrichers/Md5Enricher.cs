using System.Security.Cryptography;

namespace CodeIndexer.Services.Enrichers;

public sealed class Md5Enricher : IMetadataEnricher
{
    public string Name => "Md5";

    public Task<Dictionary<string, object>> EnrichAsync(string filePath, string content, CancellationToken ct = default)
    {
        string hash;
        try
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(File.ReadAllBytes(filePath));
            hash = Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch { hash = ""; }

        return Task.FromResult(new Dictionary<string, object> { ["md5"] = hash });
    }
}
