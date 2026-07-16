using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models;
using LlamaIntegrationAPI.Models.Response;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models;
using System.Text;
using System.Text.Json;

namespace LlamaIntegrationAPI.Services
{
    public interface IOllamaService
    {
        Task<IResponse> ExtractInfoAsync(GenerateRequest request);
    }

    public class OllamaService : IOllamaService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OllamaService> _logger;

        public OllamaService(HttpClient httpClient, IConfiguration config, ILogger<OllamaService> logger)
        {
            var host = config["OLLAMA_HOST"] ?? "http://localhost:11434";

            _httpClient = httpClient;
            _logger = logger;
            _httpClient.BaseAddress = new Uri(host);
            _httpClient.Timeout = TimeSpan.FromHours(3);
        }

        public async Task<IResponse> ExtractInfoAsync(GenerateRequest request)
        {
            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "api/generate")
            {
                Content = content
            };

            using var response = await _httpClient.SendAsync(
                requestMessage,
                request.Stream ? HttpCompletionOption.ResponseHeadersRead
                   : HttpCompletionOption.ResponseContentRead,
                CancellationToken.None
            );

            if (!response.IsSuccessStatusCode)
                return ResponseHandler.Error($"Llama API returned status code {response.StatusCode}");

            var responseStream = await response.Content.ReadFromJsonAsync<GenerateResponseStream>();

            if (responseStream?.Response is null)
                return ResponseHandler.Error("Ollama returned an empty response.");

            var rawOutput = responseStream.Response;

            // If no JSON format was requested, return raw text
            if (request.Format is null || string.IsNullOrEmpty(request.Format?.ToString()))
                return ResponseHandler.Success(rawOutput);

            // Multi-strategy JSON extraction
            var parsed = JsonSanitizer.TryExtractJson(rawOutput);
            if (parsed.HasValue)
                return ResponseHandler.Success(parsed.Value);

            _logger.LogWarning("Failed to extract valid JSON from LLM response. Raw: {Raw}", rawOutput[..Math.Min(rawOutput.Length, 500)]);
            return ResponseHandler.Success(rawOutput);
        }
    }
}
