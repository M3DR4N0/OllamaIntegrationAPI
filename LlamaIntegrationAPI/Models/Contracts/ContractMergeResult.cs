using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Models.Contracts;

public record ContractMergeResult
{
    [JsonPropertyName("answer")]
    public string Answer { get; init; } = string.Empty;

    [JsonPropertyName("answerFormat")]
    public string AnswerFormat { get; init; } = "markdown";

    [JsonPropertyName("ollamaAnswer")]
    public string? OllamaAnswer { get; init; }

    [JsonPropertyName("geminiAnswer")]
    public string? GeminiAnswer { get; init; }

    [JsonPropertyName("ollamaError")]
    public string? OllamaError { get; init; }

    [JsonPropertyName("geminiError")]
    public string? GeminiError { get; init; }

    [JsonPropertyName("documentsProcessed")]
    public int DocumentsProcessed { get; init; }

    [JsonPropertyName("baseDocumentName")]
    public string BaseDocumentName { get; init; } = string.Empty;

    [JsonPropertyName("wordDocument")]
    public byte[]? WordDocument { get; init; }

    [JsonPropertyName("wordDocumentFileName")]
    public string? WordDocumentFileName { get; init; }

    [JsonPropertyName("wordDocumentContentType")]
    public string? WordDocumentContentType { get; init; }
}
