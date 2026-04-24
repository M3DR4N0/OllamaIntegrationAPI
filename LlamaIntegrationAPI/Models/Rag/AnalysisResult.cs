using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Models.Rag;

public record AnalysisResult
{
    [JsonPropertyName("compliance")]
    public bool Compliance { get; init; }

    [JsonPropertyName("risks")]
    public List<string> Risks { get; init; } = [];

    [JsonPropertyName("related_articles")]
    public List<string> RelatedArticles { get; init; } = [];

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;
}
