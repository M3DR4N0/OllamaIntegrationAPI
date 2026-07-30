using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Models.Rag;

public record AnalysisResult
{
    [JsonPropertyName("answer")]
    public string Answer { get; init; } = string.Empty;

    [JsonPropertyName("answerFormat")]
    public string? AnswerFormat { get; init; }

    [JsonPropertyName("ollamaAnswer")]
    public string OllamaAnswer { get; init; } = string.Empty;

    [JsonPropertyName("geminiAnswer")]
    public string? GeminiAnswer { get; init; }

    [JsonPropertyName("wordDocument")]
    public byte[]? WordDocument { get; init; }

    [JsonPropertyName("wordDocumentFileName")]
    public string? WordDocumentFileName { get; init; }

    [JsonPropertyName("wordDocumentContentType")]
    public string? WordDocumentContentType { get; init; }
}
