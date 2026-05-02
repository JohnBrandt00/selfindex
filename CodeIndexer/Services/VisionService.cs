using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CodeIndexer.Services;

public class VisionService
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<VisionService> _logger;

    public string Model { get; }
    public bool IsConfigured => !string.IsNullOrEmpty(Model);

    public VisionService(IHttpClientFactory factory, IConfiguration config, ILogger<VisionService> logger)
    {
        _factory = factory;
        _logger  = logger;
        Model    = config["Ollama:VisionModel"] ?? "";
    }

    public async Task<string?> DescribeAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (!IsConfigured || imageBytes.Length < 1024) return null;
        try
        {
            var client = _factory.CreateClient("ollama");
            var resp = await client.PostAsJsonAsync("/api/generate", new
            {
                model  = Model,
                prompt = "Describe this image thoroughly for full-text search indexing. " +
                         "Transcribe all visible text exactly. Describe diagrams, charts, " +
                         "tables, screenshots, and any other visual content in detail.",
                images = new[] { Convert.ToBase64String(imageBytes) },
                stream = false
            }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Vision call returned {Status}: {Body}", (int)resp.StatusCode, body);
                return null;
            }
            var result = await resp.Content.ReadFromJsonAsync<GenerateResp>(ct);
            return result?.Response?.Trim() is { Length: > 0 } s ? s : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision call failed ({Bytes} bytes)", imageBytes.Length);
            return null;
        }
    }

    private class GenerateResp
    {
        [JsonPropertyName("response")] public string? Response { get; set; }
    }
}
