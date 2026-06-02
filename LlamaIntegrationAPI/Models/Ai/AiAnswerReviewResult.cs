namespace LlamaIntegrationAPI.Models.Ai;

public record AiAnswerReviewResult
{
    public string FinalAnswer { get; init; } = string.Empty;
    public string OllamaAnswer { get; init; } = string.Empty;
    public string? GeminiAnswer { get; init; }
}
