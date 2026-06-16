using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LlamaIntegrationAPI.Models.Ai;
using Microsoft.Extensions.Options;

namespace LlamaIntegrationAPI.Services.Ai.Providers;

public class OpenAiCompatibleProvider(
    HttpClient httpClient,
    IOptionsMonitor<AiOptions> optionsMonitor,
    ILogger<OpenAiCompatibleProvider> logger) : BaseAiProvider(httpClient, optionsMonitor), IAiProvider
{
    public override string ProviderName => "OpenAiCompatible";

    public override async Task<AiGenerateResponse> GenerateAsync(
        AiGenerateRequest request,
        CancellationToken cancellationToken)
    {
        var selectedProvider = !string.IsNullOrWhiteSpace(request.Provider)
            ? request.Provider.Trim()
            : TryGetMetadataString(request.Metadata, "provider") ?? ProviderName;
        var providerOptions = GetProviderOptions(selectedProvider);
        var model = ResolveModel(request, selectedProvider);
        var stopwatch = StartTimer();

        if (string.IsNullOrWhiteSpace(providerOptions.ApiKey))
        {
            return CreateErrorResponse(
                selectedProvider,
                model,
                $"{selectedProvider} API key is missing. Configure Ai:Providers:{selectedProvider}:ApiKey.",
                stopwatch.Elapsed);
        }

        if (string.IsNullOrWhiteSpace(providerOptions.BaseUrl) || string.IsNullOrWhiteSpace(model))
        {
            return CreateErrorResponse(
                selectedProvider,
                model,
                $"{selectedProvider} provider configuration is incomplete. BaseUrl and Model are required.",
                stopwatch.Elapsed);
        }

        var requestUri = $"{providerOptions.BaseUrl.TrimEnd('/')}/chat/completions";
        var payload = new
        {
            model,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = request.SystemInstruction ?? string.Empty
                },
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
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerOptions.ApiKey);

            logger.LogInformation(
                "Calling OpenAI-compatible provider '{Provider}' with task '{Task}' and model '{Model}'.",
                selectedProvider,
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
                    selectedProvider,
                    model,
                    BuildHttpErrorMessage(selectedProvider, response.StatusCode),
                    stopwatch.Elapsed,
                    rawResponse);
            }

            using var json = JsonDocument.Parse(rawResponse);
            var root = json.RootElement;
            var text = ExtractText(root);

            if (string.IsNullOrWhiteSpace(text))
            {
                return CreateErrorResponse(
                    selectedProvider,
                    model,
                    $"{selectedProvider} returned an empty or unexpected response.",
                    stopwatch.Elapsed,
                    rawResponse);
            }

            var inputTokens = TryReadInt(root, "usage", "prompt_tokens");
            var outputTokens = TryReadInt(root, "usage", "completion_tokens");

            return CreateSuccessResponse(
                selectedProvider,
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
                selectedProvider,
                model,
                $"{selectedProvider} request timed out before the provider returned a response.",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "{Provider} HTTP request failed.", selectedProvider);
            return CreateErrorResponse(
                selectedProvider,
                model,
                $"{selectedProvider} request failed: {ex.Message}",
                stopwatch.Elapsed);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "{Provider} response could not be parsed.", selectedProvider);
            return CreateErrorResponse(
                selectedProvider,
                model,
                $"{selectedProvider} returned malformed JSON: {ex.Message}",
                stopwatch.Elapsed);
        }
    }

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content))
                continue;

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString()?.Trim() ?? string.Empty;

            if (content.ValueKind != JsonValueKind.Array)
                continue;

            var textParts = content
                .EnumerateArray()
                .Where(item =>
                    item.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("text", out _))
                .Select(item => item.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text));

            var text = string.Join("\n", textParts).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
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
