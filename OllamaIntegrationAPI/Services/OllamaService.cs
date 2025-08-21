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
            _httpClient.BaseAddress = new Uri(config["OLLAMA_HOST"] ?? "http://localhost:11434");
            _httpClient.Timeout = TimeSpan.FromHours(1); // Set a longer timeout for Ollama requests
        }

        public async Task<IResponse> ExtractContractInfoAsync(OllamaRequest request)
        {
            var encoding = GptEncoding.GetEncoding("cl100k_base");
            var tokens = encoding.Encode(request.Prompt);

            var tokenCount = tokens.Count + 2000; // Adding a buffer for the response

            var payload = new
            {
                model = request.Model,
                prompt = request.Prompt,
                stream = request.Stream,
                options = new
                {
                    num_ctx = tokenCount,
                    temperature = 0,      // extracción factual
                    top_p = 0.9,
                    //num_predict = 512     // límite salida
                },
                format = request.Format
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "api/generate")
            {
                Content = content
            };

            using var response = await _httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
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
                    if (chunk.RootElement.TryGetProperty("response", out var resp))
                        sb.Append(resp.GetString());
                }

                return ResponseHandler.Success(sb.ToString());
            }
            else
            {
                var jsonString = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(jsonString);

                var rawOutput = doc.RootElement.GetProperty("response").GetString();
                return ResponseHandler.Success(rawOutput);
            }
        }

        //public async Task<IResponse> ExtractContractInfoAsync(OllamaRequest request)
        //{
        //    var tokenCount = GptEncoding.GetEncoding("cl100k_base").CountTokens(request.Prompt) + 2000;

        //    object payload;

        //    if (request.Format is not null)
        //    {
        //        payload = new
        //        {
        //            request.Model,
        //            request.Prompt,
        //            request.Stream,
        //            Options = new
        //            {
        //                num_ctx = tokenCount
        //            },
        //            request.Format,
        //        };
        //    }
        //    else
        //    {
        //        payload = new
        //        {
        //            request.Model,
        //            request.Prompt,
        //            request.Stream,
        //            Options = new
        //            {
        //                num_ctx = tokenCount
        //            },
        //        };
        //    }

        //    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        //    var response = await _httpClient.PostAsync("api/generate", content);
        //    response.EnsureSuccessStatusCode();

        //    if (response.StatusCode != System.Net.HttpStatusCode.OK)
        //    {
        //        return ResponseHandler.Error($"Ollama API returned status code {response.StatusCode}");
        //    }

        //    var jsonString = await response.Content.ReadAsStringAsync();
        //    var ollamaResponse = JsonDocument.Parse(jsonString);

        //    string rawOutput = ollamaResponse.RootElement.GetProperty("response").GetString() ?? "{}";

        //    return ResponseHandler.Success(JsonSerializer.Deserialize<dynamic>(rawOutput));
        //}

    }
}
