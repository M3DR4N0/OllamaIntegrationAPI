using OllamaIntegrationAPI.Models;
using OllamaIntegrationAPI.Models.Response;
using SharpToken;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;

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
            _httpClient.BaseAddress = new Uri("http://localhost:11220");
            _httpClient.Timeout = TimeSpan.FromHours(1); // Set a longer timeout for Ollama requests
        }

        public async Task<IResponse> ExtractContractInfoAsync(OllamaRequest request)
        {
            var content = new StringContent(JsonSerializer.Serialize(request.Payload), Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
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
                return ResponseHandler.Error($"Ollama API returned status code {response.StatusCode}");

            if (request.Stream)
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                var sb = new StringBuilder();

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var chunk = JsonDocument.Parse(line);
                    if (chunk.RootElement.TryGetProperty("content", out var resp))
                        sb.Append(resp.GetString());
                }

                return ResponseHandler.Success(sb.ToString());
            }
            else
            {
                var jsonString = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(jsonString);

                var rawOutput = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                return ResponseHandler.Success(TryParseJson(rawOutput!));
            }
        }

        private static object TryParseJson(string rawOutput)
        {
            try
            {
                _ = JsonDocument.Parse(rawOutput);

                return JsonSerializer.Deserialize<dynamic>(rawOutput!)!;
            }
            catch
            {
                return rawOutput;
            }

            
        }
    }
}
