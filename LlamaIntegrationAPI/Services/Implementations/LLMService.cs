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
    private readonly int _maxNumCtx;
    private readonly int _responseBuffer;

    public LLMService(HttpClient httpClient, IConfiguration config, ILogger<LLMService> logger)
    {
        var host = config["OLLAMA_HOST"] ?? "http://localhost:11434";

        _httpClient = httpClient;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(host);
        _httpClient.Timeout = TimeSpan.FromHours(1);

        // Read a hard cap from config; default 8192 keeps gemma3:1b from reloading its KV-cache.
        _maxNumCtx = int.TryParse(config["LLM_MAX_NUM_CTX"], out var cap) ? cap : 8192;
        // Output-token budget: how many tokens we reserve for the model's reply.
        _responseBuffer = int.TryParse(config["LLM_RESPONSE_BUFFER"], out var buf) ? buf : 512;
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

        _logger.LogInformation("[LLMService] Sending generate request — model: {Model} | num_ctx: {NumCtx}", model, numCtx);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var response = await _httpClient.PostAsync("api/generate", content, ct);
        sw.Stop();

        _logger.LogInformation("[LLMService] Ollama responded in {Ms} ms — status: {Status}", sw.ElapsedMilliseconds, response.StatusCode);

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
    /// Counts tokens in the combined prompt, adds a response buffer, then clamps to
    /// <c>LLM_MAX_NUM_CTX</c> (default 8192). A stable <c>num_ctx</c> prevents Ollama
    /// from evicting and reloading the model's KV-cache on every request.
    /// </summary>
    private int CalculateNumCtx(string systemPrompt, string userPrompt)
    {
        var encoding = GptEncoding.GetEncoding("cl100k_base");
        var inputTokens = encoding.CountTokens(systemPrompt) + encoding.CountTokens(userPrompt);
        var needed = inputTokens + _responseBuffer;
        var clamped = Math.Min(needed, _maxNumCtx);

        _logger.LogInformation(
            "[LLMService] numCtx — input: {Input} tokens | buffer: {Buffer} | needed: {Needed} | clamped to: {Clamped} (max: {Max})",
            inputTokens, _responseBuffer, needed, clamped, _maxNumCtx);

        if (needed > _maxNumCtx)
            _logger.LogWarning(
                "[LLMService] Prompt ({Needed} tokens) exceeds LLM_MAX_NUM_CTX ({Max}). " +
                "The context will be TRUNCATED. Reduce MaxContractChunks or increase LLM_MAX_NUM_CTX.",
                needed, _maxNumCtx);

        return clamped;
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }
}
