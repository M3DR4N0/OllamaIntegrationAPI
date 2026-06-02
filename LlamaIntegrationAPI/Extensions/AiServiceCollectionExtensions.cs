using LlamaIntegrationAPI.Models.Ai;
using LlamaIntegrationAPI.Services.Ai;
using LlamaIntegrationAPI.Services.Ai.Providers;

namespace LlamaIntegrationAPI.Extensions;

public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAiServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection("Ai"));
        services.PostConfigure<AiOptions>(options =>
        {
            options.DefaultProvider = string.IsNullOrWhiteSpace(options.DefaultProvider)
                ? "Gemini"
                : options.DefaultProvider.Trim();

            options.DefaultLanguage = string.IsNullOrWhiteSpace(options.DefaultLanguage)
                ? "es"
                : options.DefaultLanguage.Trim();

            options.TimeoutSeconds = options.TimeoutSeconds <= 0 ? 30 : options.TimeoutSeconds;
            options.MaxTokens = options.MaxTokens <= 0 ? 4096 : options.MaxTokens;
            options.ReviewMaxTokens = options.ReviewMaxTokens <= 0 ? 8192 : options.ReviewMaxTokens;
            options.Providers = options.Providers is null
                ? new Dictionary<string, AiProviderOptions>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, AiProviderOptions>(options.Providers, StringComparer.OrdinalIgnoreCase);

            EnsureProvider(options, "Gemini", "https://generativelanguage.googleapis.com", "gemini-2.5-flash", 8192);
            EnsureProvider(options, "Claude", "https://api.anthropic.com", "claude-sonnet-4-5", 8192);
            EnsureProvider(options, "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", 8192);

            ApplyApiKeyFallback(options, "Gemini", "Gemini__ApiKey");
            ApplyApiKeyFallback(options, "Claude", "Claude__ApiKey");
            ApplyApiKeyFallback(options, "Groq", "Groq__ApiKey");
        });

        services.AddHttpClient<GeminiAiProvider>();
        services.AddHttpClient<ClaudeAiProvider>();
        services.AddHttpClient<OpenAiCompatibleProvider>();

        services.AddTransient<IAiProvider>(serviceProvider => serviceProvider.GetRequiredService<GeminiAiProvider>());
        services.AddTransient<IAiProvider>(serviceProvider => serviceProvider.GetRequiredService<ClaudeAiProvider>());
        services.AddTransient<IAiProvider>(serviceProvider => serviceProvider.GetRequiredService<OpenAiCompatibleProvider>());

        services.AddScoped<IAiGatewayService, AiGatewayService>();
        services.AddScoped<IAiAnswerReviewService, AiAnswerReviewService>();

        return services;
    }

    private static void EnsureProvider(
        AiOptions options,
        string providerName,
        string defaultBaseUrl,
        string defaultModel,
        int defaultMaxTokens)
    {
        if (!options.Providers.TryGetValue(providerName, out var providerOptions))
        {
            providerOptions = new AiProviderOptions();
            options.Providers[providerName] = providerOptions;
        }

        providerOptions.BaseUrl = string.IsNullOrWhiteSpace(providerOptions.BaseUrl)
            ? defaultBaseUrl
            : providerOptions.BaseUrl.Trim();

        providerOptions.Model = string.IsNullOrWhiteSpace(providerOptions.Model)
            ? defaultModel
            : providerOptions.Model.Trim();

        providerOptions.MaxTokens = providerOptions.MaxTokens is > 0
            ? providerOptions.MaxTokens
            : defaultMaxTokens;

        providerOptions.ApiKey ??= string.Empty;
    }

    private static void ApplyApiKeyFallback(AiOptions options, string providerName, string environmentVariableName)
    {
        if (!options.Providers.TryGetValue(providerName, out var providerOptions))
            return;

        if (!string.IsNullOrWhiteSpace(providerOptions.ApiKey))
            return;

        providerOptions.ApiKey = Environment.GetEnvironmentVariable(environmentVariableName) ?? string.Empty;
    }
}
