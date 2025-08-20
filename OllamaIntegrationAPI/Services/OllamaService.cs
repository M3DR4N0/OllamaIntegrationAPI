using OllamaIntegrationAPI.Models;
using OllamaIntegrationAPI.Models.Response;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OllamaIntegrationAPI.Services
{
    public interface IOllamaService
    {
        Task<IResponse> ExtractContractInfoAsync(OllamaRequest request);
    }

    public class OllamaService : IOllamaService
    {
        private readonly HttpClient _httpClient;

        public OllamaService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri(config["OLLAMA_HOST"] ?? "http://localhost:11434");
            _httpClient.Timeout = TimeSpan.FromHours(1); // Set a longer timeout for Ollama requests
        }

        public async Task<IResponse> ExtractContractInfoAsync(OllamaRequest request)
        {
            int palabras = request.Prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            // Aproximación de tokens
            double tokens = (palabras / 0.75) + 1000;

            var payload = new
            {
                request.Model,
                request.Prompt,
                request.Format,
                request.Stream,
                Options = new
                {
                    num_ctx = tokens
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("api/generate", content);
            response.EnsureSuccessStatusCode();

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return ResponseHandler.Error($"Ollama API returned status code {response.StatusCode}");
            }

            var jsonString = await response.Content.ReadAsStringAsync();
            var ollamaResponse = JsonDocument.Parse(jsonString);

            string rawOutput = ollamaResponse.RootElement.GetProperty("response").GetString() ?? "{}";

            return ResponseHandler.Success(JsonSerializer.Deserialize<dynamic>(rawOutput));
        }

    }
}
