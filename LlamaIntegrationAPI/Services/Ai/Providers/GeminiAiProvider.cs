using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LlamaIntegrationAPI.Models.Ai;
using Microsoft.Extensions.Options;

namespace LlamaIntegrationAPI.Services.Ai.Providers;

public class GeminiAiProvider(
    HttpClient httpClient,
    IOptionsMonitor<AiOptions> optionsMonitor,
    ILogger<GeminiAiProvider> logger) : BaseAiProvider(httpClient, optionsMonitor), IAiProvider
{
    public override string ProviderName => "Gemini";

    public override async Task<AiGenerateResponse> GenerateAsync(
        AiGenerateRequest request,
        CancellationToken cancellationToken)
    {
        var providerOptions = GetProviderOptions(ProviderName);
        var model = ResolveModel(request, ProviderName);
        var stopwatch = StartTimer();

        if (string.IsNullOrWhiteSpace(providerOptions.ApiKey))
        {
            return CreateErrorResponse(
                ProviderName,
                model,
                "Gemini API key is missing. Configure Ai:Providers:Gemini:ApiKey or Gemini__ApiKey.",
                stopwatch.Elapsed);
        }

        if (string.IsNullOrWhiteSpace(providerOptions.BaseUrl) || string.IsNullOrWhiteSpace(model))
        {
            return CreateErrorResponse(
                ProviderName,
                model,
                "Gemini provider configuration is incomplete. BaseUrl and Model are required.",
                stopwatch.Elapsed);
        }

        var requestUri =
            $"{providerOptions.BaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(providerOptions.ApiKey)}";

        var payload = new
        {
            systemInstruction = new
            {
                parts = new[]
                {
                    new { text = request.SystemInstruction ?? string.Empty }
                }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[]
                    {
                        new { text = BuildUserPrompt(request) }
                    }
                }
            },
            generationConfig = new
            {
                temperature = request.Temperature,
                maxOutputTokens = request.MaxTokens
            }
        };

        try
        {
            using var timeoutScope = CreateTimeoutScope(cancellationToken);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json")
            };

            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            logger.LogInformation(
                "Calling Gemini provider with task '{Task}' and model '{Model}'.",
                request.Task,
                model);

            using var response = await HttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutScope.Token).ConfigureAwait(false);

            var rawResponse = await response.Content.ReadAsStringAsync(timeoutScope.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return CreateErrorResponse(
                    ProviderName,
                    model,
                    BuildHttpErrorMessage(ProviderName, response.StatusCode),
                    stopwatch.Elapsed,
                    rawResponse);
            }

            using var json = JsonDocument.Parse(rawResponse);
            var root = json.RootElement;

            var text = ExtractGeminiText(root);
            if (string.IsNullOrWhiteSpace(text))
            {
                return CreateErrorResponse(
                    ProviderName,
                    model,
                    "Gemini returned an empty or unexpected response.",
                    stopwatch.Elapsed,
                    rawResponse);
            }

            var inputTokens = TryReadInt(root, "usageMetadata", "promptTokenCount");
            var outputTokens = TryReadInt(root, "usageMetadata", "candidatesTokenCount");

            return CreateSuccessResponse(
                ProviderName,
                model,
                text,
                rawResponse,
                stopwatch.Elapsed,
                inputTokens,
                outputTokens);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateErrorResponse(
                ProviderName,
                model,
                "Gemini request timed out before the provider returned a response.",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Gemini HTTP request failed.");
            return CreateErrorResponse(
                ProviderName,
                model,
                $"Gemini request failed: {ex.Message}",
                stopwatch.Elapsed);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Gemini response could not be parsed.");
            return CreateErrorResponse(
                ProviderName,
                model,
                $"Gemini returned malformed JSON: {ex.Message}",
                stopwatch.Elapsed);
        }
    }

    private static string ExtractGeminiText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
                continue;

            var textParts = parts
                .EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text));

            var text = string.Join("\n", textParts);
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return string.Empty;
    }

    private static int? TryReadInt(JsonElement root, string sectionName, string propertyName)
    {
        if (!root.TryGetProperty(sectionName, out var section) ||
            !section.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number)
            return null;

        return value.TryGetInt32(out var parsed) ? parsed : null;
    }
}
