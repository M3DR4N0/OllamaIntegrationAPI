using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LlamaIntegrationAPI.Models.Ai;
using Microsoft.Extensions.Options;

namespace LlamaIntegrationAPI.Services.Ai;

public abstract class BaseAiProvider(
    HttpClient httpClient,
    IOptionsMonitor<AiOptions> optionsMonitor)
{
    protected HttpClient HttpClient { get; } = httpClient;

    protected AiOptions CurrentOptions => optionsMonitor.CurrentValue;

    public abstract string ProviderName { get; }

    public virtual string ModelName => GetProviderOptions(ProviderName).Model;

    public abstract Task<AiGenerateResponse> GenerateAsync(
        AiGenerateRequest request,
        CancellationToken cancellationToken);

    protected AiProviderOptions GetProviderOptions(string providerKey)
    {
        if (CurrentOptions.Providers.TryGetValue(providerKey, out var providerOptions))
            return providerOptions;

        if (CurrentOptions.Providers.TryGetValue(ProviderName, out providerOptions))
            return providerOptions;

        return new AiProviderOptions();
    }

    protected string ResolveModel(AiGenerateRequest request, string providerKey)
    {
        if (!string.IsNullOrWhiteSpace(request.Model))
            return request.Model.Trim();

        return GetProviderOptions(providerKey).Model;
    }

    protected CancellationTokenSource CreateTimeoutScope(CancellationToken cancellationToken)
    {
        var timeoutSeconds = CurrentOptions.TimeoutSeconds <= 0 ? 30 : CurrentOptions.TimeoutSeconds;
        var timeoutScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutScope.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return timeoutScope;
    }

    protected static Stopwatch StartTimer() => Stopwatch.StartNew();

    protected static string BuildUserPrompt(AiGenerateRequest request)
    {
        var sections = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Task))
            sections.Add($"Task:\n{request.Task.Trim()}");

        if (!string.IsNullOrWhiteSpace(request.Context))
            sections.Add($"Context:\n{request.Context.Trim()}");

        sections.Add($"Prompt:\n{request.Prompt?.Trim() ?? string.Empty}");

        return string.Join("\n\n", sections);
    }

    protected static AiGenerateResponse CreateSuccessResponse(
        string provider,
        string model,
        string text,
        string? rawResponse,
        TimeSpan duration,
        int? inputTokens = null,
        int? outputTokens = null)
    {
        return new AiGenerateResponse
        {
            Success = true,
            Provider = provider,
            Model = model,
            Text = text,
            RawResponse = rawResponse,
            Duration = duration,
            InputTokens = inputTokens,
            OutputTokens = outputTokens
        };
    }

    protected static AiGenerateResponse CreateErrorResponse(
        string provider,
        string model,
        string error,
        TimeSpan duration,
        string? rawResponse = null)
    {
        return new AiGenerateResponse
        {
            Success = false,
            Provider = provider,
            Model = model,
            Text = string.Empty,
            Error = error,
            RawResponse = rawResponse,
            Duration = duration
        };
    }

    public static string? TryGetMetadataString(Dictionary<string, object>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String =>
                jsonElement.GetString(),
            JsonElement jsonElement => jsonElement.ToString(),
            _ => value.ToString()
        };
    }

    protected static string BuildHttpErrorMessage(string provider, HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"{provider} returned 401 Unauthorized. Check the configured API key.",
            HttpStatusCode.TooManyRequests =>
                $"{provider} returned 429 Too Many Requests. Try again later or use another provider.",
            HttpStatusCode.RequestTimeout =>
                $"{provider} timed out while processing the request.",
            HttpStatusCode.InternalServerError =>
                $"{provider} returned 500 Internal Server Error.",
            _ => $"{provider} returned HTTP {(int)statusCode} ({statusCode})."
        };
    }
}
