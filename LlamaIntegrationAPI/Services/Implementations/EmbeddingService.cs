using LlamaIntegrationAPI.Services.Interfaces;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Services.Implementations;

/// <summary>
/// Generates embeddings using the Ollama /api/embed endpoint.
/// Configure via appsettings: EMBEDDING_MODEL and EMBEDDING_DIMENSIONS.
/// </summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly string _model;

    public int Dimensions { get; }

    public EmbeddingService(HttpClient httpClient, IConfiguration config, ILogger<EmbeddingService> logger)
    {
        var host = config["OLLAMA_HOST"] ?? "http://localhost:11434";

        _httpClient = httpClient;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(host);
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
        _model = config["EMBEDDING_MODEL"] ?? "nomic-embed-text";
        Dimensions = int.TryParse(config["EMBEDDING_DIMENSIONS"], out var dim) ? dim : 768;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var result = await GenerateEmbeddingsAsync([text], ct);
        return result[0];
    }

    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var input = texts.ToList();

        if (input.Count == 0)
            return [];

        _logger.LogInformation("Generating embeddings for {Count} text(s) with model {Model}", input.Count, _model);

        var payload = new { model = _model, input };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("api/embed", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Ollama embed failed ({response.StatusCode}): {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct)
            ?? throw new InvalidOperationException("Ollama returned null embedding response.");

        return result.Embeddings;
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; set; } = [];
    }
}
