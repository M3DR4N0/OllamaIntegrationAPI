namespace LlamaIntegrationAPI.Models.Rag;

public record ChunkMetadata
{
    public string DocumentName { get; init; } = string.Empty;
    public string DocumentType { get; init; } = string.Empty;
    public string? Section { get; init; }
    public string? Article { get; init; }
    public string Source { get; init; } = string.Empty;
}
