using System.Text;
using LlamaIntegrationAPI.Models.Ai;
using Microsoft.Extensions.Options;

namespace LlamaIntegrationAPI.Services.Ai;

public class AiGatewayService(
    IEnumerable<IAiProvider> providers,
    IOptionsMonitor<AiOptions> optionsMonitor,
    ILogger<AiGatewayService> logger) : IAiGatewayService
{
    private const string BaseSystemInstruction =
        "Eres un asistente integrado dentro de una API empresarial. " +
        "Tu tarea es ayudar a procesar, mejorar, traducir, resumir o responder consultas de forma precisa. " +
        "Responde siempre en espanol claro y profesional, salvo que se indique explicitamente otro idioma. " +
        "No inventes informacion. Si falta contexto, dilo claramente. " +
        "Si se solicita JSON, devuelve unicamente JSON valido.";

    private readonly Dictionary<string, IAiProvider> _providers =
        providers.ToDictionary(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase);

    public async Task<AiGenerateResponse> GenerateAsync(
        AiGenerateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return new AiGenerateResponse
            {
                Success = false,
                Provider = ResolveProviderName(request),
                Model = string.Empty,
                Error = "Prompt is required."
            };
        }

        if (!optionsMonitor.CurrentValue.UseExternalProviders)
        {
            return new AiGenerateResponse
            {
                Success = false,
                Provider = "LocalOnly",
                Model = string.Empty,
                Error = "External AI providers are disabled. Local-only mode is enabled."
            };
        }

        var providerKey = ResolveProviderName(request);
        var provider = ResolveProvider(providerKey);

        if (provider is null)
        {
            logger.LogError("No AI provider is registered for request provider '{Provider}'.", providerKey);

            return new AiGenerateResponse
            {
                Success = false,
                Provider = providerKey,
                Model = string.Empty,
                Error = $"No AI provider is registered for '{providerKey}'."
            };
        }

        var effectiveRequest = BuildEffectiveRequest(request, providerKey);

        logger.LogInformation(
            "AI gateway dispatching task '{Task}' to provider '{Provider}' using model '{Model}'.",
            effectiveRequest.Task,
            providerKey,
            effectiveRequest.Model ?? provider.ModelName);

        return await provider.GenerateAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);
    }

    private AiGenerateRequest BuildEffectiveRequest(AiGenerateRequest request, string providerKey)
    {
        var options = optionsMonitor.CurrentValue;
        var providerOptions = options.Providers.TryGetValue(providerKey, out var resolvedProviderOptions)
            ? resolvedProviderOptions
            : null;
        var metadata = request.Metadata is null
            ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(request.Metadata, StringComparer.OrdinalIgnoreCase);

        metadata["provider"] = providerKey;

        var forceSpanish = request.ForceSpanish || options.ForceSpanishResponses;
        var targetLanguage = string.IsNullOrWhiteSpace(request.TargetLanguage)
            ? (forceSpanish ? options.DefaultLanguage : null)
            : request.TargetLanguage;

        return new AiGenerateRequest
        {
            Task = request.Task,
            Prompt = request.Prompt,
            Context = request.Context,
            Provider = providerKey,
            Model = string.IsNullOrWhiteSpace(request.Model)
                ? providerOptions?.Model
                : request.Model.Trim(),
            TargetLanguage = targetLanguage,
            ForceSpanish = forceSpanish,
            Temperature = request.Temperature ?? options.Temperature,
            MaxTokens = request.MaxTokens ?? providerOptions?.MaxTokens ?? options.MaxTokens,
            Metadata = metadata,
            SystemInstruction = BuildSystemInstruction(request, targetLanguage, forceSpanish)
        };
    }

    private string BuildSystemInstruction(
        AiGenerateRequest request,
        string? targetLanguage,
        bool forceSpanish)
    {
        var builder = new StringBuilder();
        builder.AppendLine(BaseSystemInstruction);
        builder.AppendLine();
        builder.AppendLine("Rules:");
        builder.AppendLine("- Be precise.");
        builder.AppendLine("- If you do not know something, say so clearly.");
        builder.AppendLine("- Do not invent data.");
        builder.AppendLine("- If the user asks for JSON, return valid JSON only.");
        builder.AppendLine("- Do not add unnecessary filler.");

        if (forceSpanish || string.Equals(targetLanguage, "es", StringComparison.OrdinalIgnoreCase))
            builder.AppendLine("- Reply in clear Spanish.");
        else if (!string.IsNullOrWhiteSpace(targetLanguage))
            builder.AppendLine($"- Reply in {targetLanguage.Trim()}.");

        if (!string.IsNullOrWhiteSpace(request.Task))
            builder.AppendLine($"- Current task: {request.Task.Trim()}.");

        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            builder.AppendLine();
            builder.AppendLine("Additional instruction:");
            builder.AppendLine(request.SystemInstruction.Trim());
        }

        return builder.ToString().Trim();
    }

    private string ResolveProviderName(AiGenerateRequest request)
    {
        if (!optionsMonitor.CurrentValue.UseExternalProviders)
            return "LocalOnly";

        if (!string.IsNullOrWhiteSpace(request.Provider))
            return request.Provider.Trim();

        var requestedProvider = BaseAiProvider.TryGetMetadataString(request.Metadata, "provider");

        if (!string.IsNullOrWhiteSpace(requestedProvider))
            return requestedProvider.Trim();

        var defaultProvider = optionsMonitor.CurrentValue.DefaultProvider;
        return string.IsNullOrWhiteSpace(defaultProvider) ? "Gemini" : defaultProvider.Trim();
    }

    private IAiProvider? ResolveProvider(string providerKey)
    {
        if (_providers.TryGetValue(providerKey, out var exactProvider))
            return exactProvider;

        if (IsOpenAiCompatibleAlias(providerKey) &&
            _providers.TryGetValue("OpenAiCompatible", out var openAiCompatibleProvider))
            return openAiCompatibleProvider;

        var fallbackProviderName = optionsMonitor.CurrentValue.DefaultProvider;

        if (!string.IsNullOrWhiteSpace(fallbackProviderName) &&
            _providers.TryGetValue(fallbackProviderName, out var fallbackProvider))
        {
            logger.LogWarning(
                "AI provider '{RequestedProvider}' is not registered. Falling back to '{FallbackProvider}'.",
                providerKey,
                fallbackProviderName);
            return fallbackProvider;
        }

        if (_providers.TryGetValue("Gemini", out var geminiProvider))
            return geminiProvider;

        return _providers.Values.FirstOrDefault();
    }

    private bool IsOpenAiCompatibleAlias(string providerKey)
    {
        if (string.Equals(providerKey, "Gemini", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerKey, "Claude", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(providerKey, "OpenAiCompatible", StringComparison.OrdinalIgnoreCase))
            return true;

        return optionsMonitor.CurrentValue.Providers.ContainsKey(providerKey);
    }
}
