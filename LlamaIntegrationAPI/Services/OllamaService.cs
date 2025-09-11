using LlamaIntegrationAPI.Models;
using LlamaIntegrationAPI.Models.Response;
using System.Text;
using System.Text.Json;

namespace LlamaIntegrationAPI.Services
{
    public interface ILlamaService
    {
        Task<IResponse> ExtractContractInfoAsync(LlamaRequest request);
    }

    public class LlamaService : ILlamaService
    {
        private readonly HttpClient _httpClient;

        public LlamaService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri(config["LLAMA_HOST"] ?? "http://localhost:11220");
            _httpClient.Timeout = TimeSpan.FromHours(1); // Set a longer timeout for Llama requests
        }

        public async Task<IResponse> ExtractContractInfoAsync(LlamaRequest request)
        {
            var content = new StringContent(JsonSerializer.Serialize(request.Payload), Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
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
