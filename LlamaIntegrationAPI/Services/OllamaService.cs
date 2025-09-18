using LlamaIntegrationAPI.Models;
using LlamaIntegrationAPI.Models.Response;
using OllamaSharp;
using OllamaSharp.Models;
using System.Net.Http;
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

        public OllamaService(HttpClient httpClient, IConfiguration config)
        {
            var host = config["OLLAMA_HOST"] ?? "http://localhost:11434";

            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri(host);
            _httpClient.Timeout = TimeSpan.FromHours(1); // Set a longer timeout for Llama requests
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
           
            var rawOutput = responseStream.Response;
            return ResponseHandler.Success(TryParseJson(rawOutput!));
            
        }

        private static JsonElement ParseClean(string llmResponse)
        {
            // quita fences si vienen
            if (llmResponse.StartsWith("```"))
            {
                var firstNL = llmResponse.IndexOf('\n');
                if (firstNL >= 0) llmResponse = llmResponse[(firstNL + 1)..];
                var lastFence = llmResponse.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) llmResponse = llmResponse[..lastFence];
            }

            // recorta del primer '{' al último '}'
            int i = llmResponse.IndexOf('{'), j = llmResponse.LastIndexOf('}');
            var json = (i >= 0 && j > i) ? llmResponse.Substring(i, j - i + 1).Trim() : "{}";

            return JsonDocument.Parse(json).RootElement;
        }

        private static object TryParseJson(string rawOutput)
        {
            try
            {
                return ParseClean(rawOutput);
            }
            catch
            {
                return rawOutput;
            }
        }
    }
}
