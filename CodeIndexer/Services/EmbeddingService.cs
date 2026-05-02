using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeIndexer.Services;

public class EmbeddingService
{
    private readonly IHttpClientFactory _factory;
    private readonly string _model;
    private readonly ILogger<EmbeddingService> _logger;

    private int _dimensions;
    public int Dimensions => _dimensions;
    public string Model => _model;

    // Which endpoint this model actually supports — detected on first successful call
    private enum EmbedApi { Unknown, NewEmbed, OldEmbeddings }
    private EmbedApi _api = EmbedApi.Unknown;

    public EmbeddingService(IHttpClientFactory factory, IConfiguration config, ILogger<EmbeddingService> logger)
    {
        _factory = factory;
        _model = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
        _logger = logger;
    }

    // nomic-embed-text context window is 8192 tokens; ~6 chars/token is conservative
    private const int MaxInputChars = 8000;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (text.Length > MaxInputChars)
            text = text[..MaxInputChars];

        // If we already know which API works, use it directly
        if (_api == EmbedApi.NewEmbed)  return await CallNewApi(text, ct);
        if (_api == EmbedApi.OldEmbeddings) return await CallOldApi(text, ct);

        // Auto-detect: try new API first, fall back to old
        try
        {
            var result = await CallNewApi(text, ct);
            _api = EmbedApi.NewEmbed;
            _logger.LogInformation("Embedding API: POST /api/embed (new)");
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("POST /api/embed failed ({Status}), trying /api/embeddings", ex.StatusCode);
        }

        var fallback = await CallOldApi(text, ct);
        _api = EmbedApi.OldEmbeddings;
        _logger.LogInformation("Embedding API: POST /api/embeddings (legacy)");
        return fallback;
    }

    public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var results = new List<float[]>();
        foreach (var text in texts)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await EmbedAsync(text, ct));
        }
        return results.ToArray();
    }

    // POST /api/embed  { model, input: string }  →  { embeddings: [[...]] }
    private async Task<float[]> CallNewApi(string text, CancellationToken ct)
    {
        var client = _factory.CreateClient("ollama");
        var response = await client.PostAsJsonAsync("/api/embed",
            new { model = _model, input = text }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("/api/embed {Status}: {Body}", (int)response.StatusCode, body);
        }
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<NewEmbedResponse>(ct);
        if (result?.Embeddings is not { Length: > 0 })
            throw new InvalidOperationException("Ollama /api/embed returned empty embeddings array");
        var vec = result.Embeddings[0];
        if (_dimensions == 0) _dimensions = vec.Length;
        return vec;
    }

    // POST /api/embeddings  { model, prompt: string }  →  { embedding: [...] }
    private async Task<float[]> CallOldApi(string text, CancellationToken ct)
    {
        var client = _factory.CreateClient("ollama");
        var response = await client.PostAsJsonAsync("/api/embeddings",
            new { model = _model, prompt = text }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("/api/embeddings {Status}: {Body}", (int)response.StatusCode, body);
        }
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OldEmbedResponse>(ct);
        var vec = result!.Embedding;
        if (_dimensions == 0) _dimensions = vec.Length;
        return vec;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _factory.CreateClient("ollama").GetAsync("/api/tags", ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private class NewEmbedResponse
    {
        [JsonPropertyName("embeddings")] public float[][] Embeddings { get; set; } = [];
    }

    private class OldEmbedResponse
    {
        [JsonPropertyName("embedding")] public float[] Embedding { get; set; } = [];
    }
}
