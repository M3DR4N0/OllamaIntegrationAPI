using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Services.Interfaces;
using SharpToken;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Services.Implementations;

/// <summary>
/// Clean LLM abstraction over Ollama's /api/generate endpoint.
/// Supports typed deserialization via <see cref="JsonSanitizer"/>.
/// </summary>
public class LLMService : ILLMService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LLMService> _logger;

    public LLMService(HttpClient httpClient, IConfiguration config, ILogger<LLMService> logger)
    {
        var host = config["OLLAMA_HOST"] ?? "http://localhost:11434";

        _httpClient = httpClient;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(host);
        _httpClient.Timeout = TimeSpan.FromHours(1);
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, string model, CancellationToken ct = default)
    {
        var numCtx = CalculateNumCtx(systemPrompt, userPrompt);

        var payload = new
        {
            model,
            system = systemPrompt,
            prompt = userPrompt,
            stream = false,
            format = (object?)null,
            options = new { temperature = 0, num_ctx = numCtx }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("api/generate", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Ollama generate failed ({response.StatusCode}): {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct)
            ?? throw new InvalidOperationException("Ollama returned null generate response.");

        return result.Response;
    }

    public async Task<T?> GenerateAsync<T>(string systemPrompt, string userPrompt, string model, CancellationToken ct = default) where T : class
    {
        var numCtx = CalculateNumCtx(systemPrompt, userPrompt);

        // Request JSON format from the model
        var payload = new
        {
            model,
            system = systemPrompt,
            prompt = userPrompt,
            stream = false,
            format = "json",
            options = new { temperature = 0, num_ctx = numCtx }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("api/generate", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Ollama generate failed ({response.StatusCode}): {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct)
            ?? throw new InvalidOperationException("Ollama returned null generate response.");

        _logger.LogDebug("LLM raw response: {Raw}", result.Response[..Math.Min(result.Response.Length, 300)]);

        return JsonSanitizer.TryExtractJson<T>(result.Response);
    }

    /// <summary>
    /// Counts tokens in the combined prompt and adds a 2 048-token buffer for
    /// the model's response, ensuring Ollama never truncates the context window.
    /// </summary>
    private static int CalculateNumCtx(string systemPrompt, string userPrompt)
    {
        var encoding = GptEncoding.GetEncoding("cl100k_base");
        var inputTokens = encoding.CountTokens(systemPrompt) + encoding.CountTokens(userPrompt);
        return inputTokens + 2048;
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }
}
