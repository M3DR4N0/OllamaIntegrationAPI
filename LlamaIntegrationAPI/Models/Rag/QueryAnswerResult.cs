using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Models.Rag;

public record QueryAnswerResult
{
    [JsonPropertyName("answer")]
    public string Answer { get; init; } = string.Empty;

    [JsonPropertyName("ollamaAnswer")]
    public string OllamaAnswer { get; init; } = string.Empty;

    [JsonPropertyName("geminiAnswer")]
    public string? GeminiAnswer { get; init; }

    [JsonPropertyName("context_used")]
    public int ContextUsed { get; init; }

    [JsonPropertyName("intent")]
    public string Intent { get; init; } = string.Empty;
}
