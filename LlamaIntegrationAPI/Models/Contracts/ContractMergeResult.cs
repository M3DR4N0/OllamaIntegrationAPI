using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Models.Contracts;

public record ContractMergeResult
{
    [JsonPropertyName("answer")]
    public string Answer { get; init; } = string.Empty;

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
}
