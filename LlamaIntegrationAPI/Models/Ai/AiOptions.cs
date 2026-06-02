namespace LlamaIntegrationAPI.Models.Ai;

public class AiOptions
{
    public string DefaultProvider { get; set; } = "Gemini";
    public string DefaultLanguage { get; set; } = "es";
    public bool ForceSpanishResponses { get; set; } = true;
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 4096;
    public int ReviewMaxTokens { get; set; } = 8192;
    public int TimeoutSeconds { get; set; } = 30;
    public Dictionary<string, AiProviderOptions> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
