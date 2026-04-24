namespace LlamaIntegrationAPI.Models.Rag;

/// <summary>
/// A <see cref="DocumentChunk"/> paired with a relevance score (0.0 – 1.0)
/// produced by the reranking stage.
/// </summary>
public record ScoredChunk
{
    public DocumentChunk Chunk { get; init; } = default!;
    public float RelevanceScore { get; init; }
}
