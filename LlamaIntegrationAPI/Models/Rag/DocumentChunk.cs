namespace LlamaIntegrationAPI.Models.Rag;

public record DocumentChunk
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; init; } = string.Empty;
    public float[]? Embedding { get; init; }
    public ChunkMetadata Metadata { get; init; } = new();
}
