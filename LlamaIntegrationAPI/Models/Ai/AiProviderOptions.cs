namespace LlamaIntegrationAPI.Models.Ai;

public class AiProviderOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int? MaxTokens { get; set; }
}
