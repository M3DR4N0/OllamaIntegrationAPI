using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LlamaIntegrationAPI.Models.Ai;
using Microsoft.Extensions.Options;

namespace LlamaIntegrationAPI.Services.Ai.Providers;

public class ClaudeAiProvider(
    HttpClient httpClient,
    IOptionsMonitor<AiOptions> optionsMonitor,
    ILogger<ClaudeAiProvider> logger) : BaseAiProvider(httpClient, optionsMonitor), IAiProvider
{
    private const string AnthropicVersion = "2023-06-01";

    public override string ProviderName => "Claude";

    public override async Task<AiGenerateResponse> GenerateAsync(
        AiGenerateRequest request,
        CancellationToken cancellationToken)
    {
        var providerOptions = GetProviderOptions(ProviderName);
        var model = providerOptions.Model;
        var stopwatch = StartTimer();

        if (string.IsNullOrWhiteSpace(providerOptions.ApiKey))
        {
            return CreateErrorResponse(
                ProviderName,
                model,
                "Claude API key is missing. Configure Ai:Providers:Claude:ApiKey.",
                stopwatch.Elapsed);
        }

        if (string.IsNullOrWhiteSpace(providerOptions.BaseUrl) || string.IsNullOrWhiteSpace(model))
        {
            return CreateErrorResponse(
                ProviderName,
                model,
                "Claude provider configuration is incomplete. BaseUrl and Model are required.",
                stopwatch.Elapsed);
        }

        var requestUri = $"{providerOptions.BaseUrl.TrimEnd('/')}/v1/messages";
        var payload = new
        {
            model,
            system = request.SystemInstruction ?? string.Empty,
            max_tokens = request.MaxTokens,
            temperature = request.Temperature,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = BuildUserPrompt(request)
                }
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
            httpRequest.Headers.Add("x-api-key", providerOptions.ApiKey);
            httpRequest.Headers.Add("anthropic-version", AnthropicVersion);

            logger.LogInformation(
                "Calling Claude provider with task '{Task}' and model '{Model}'.",
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
            var text = ExtractClaudeText(root);

            if (string.IsNullOrWhiteSpace(text))
            {
                return CreateErrorResponse(
                    ProviderName,
                    model,
                    "Claude returned an empty or unexpected response.",
                    stopwatch.Elapsed,
                    rawResponse);
            }

            var inputTokens = TryReadInt(root, "usage", "input_tokens");
            var outputTokens = TryReadInt(root, "usage", "output_tokens");

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
                "Claude request timed out before the provider returned a response.",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Claude HTTP request failed.");
            return CreateErrorResponse(
                ProviderName,
                model,
                $"Claude request failed: {ex.Message}",
                stopwatch.Elapsed);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Claude response could not be parsed.");
            return CreateErrorResponse(
                ProviderName,
                model,
                $"Claude returned malformed JSON: {ex.Message}",
                stopwatch.Elapsed);
        }
    }

    private static string ExtractClaudeText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var textParts = content
            .EnumerateArray()
            .Where(item =>
                item.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("text", out _))
            .Select(item => item.GetProperty("text").GetString())
            .Where(text => !string.IsNullOrWhiteSpace(text));

        return string.Join("\n", textParts).Trim();
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
