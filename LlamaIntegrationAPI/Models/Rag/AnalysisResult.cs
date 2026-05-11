using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Models.Rag;

public record AnalysisResult
{
    [JsonPropertyName("answer")]
    public string Answer { get; init; } = string.Empty;
}
