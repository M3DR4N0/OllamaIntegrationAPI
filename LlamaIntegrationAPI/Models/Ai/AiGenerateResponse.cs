namespace LlamaIntegrationAPI.Models.Ai;

public class AiGenerateResponse
{
    public bool Success { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? RawResponse { get; set; }
    public string? Error { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public TimeSpan Duration { get; set; }
}
