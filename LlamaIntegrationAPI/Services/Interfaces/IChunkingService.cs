using LlamaIntegrationAPI.Models.Rag;

namespace LlamaIntegrationAPI.Services.Interfaces;

/// <summary>
/// Splits document text into chunks for embedding and retrieval.
/// Supports semantic splitting (articles/sections) with a token-based fallback.
/// </summary>
public interface IChunkingService
{
    /// <summary>
    /// Splits text into chunks, preferring semantic boundaries when detected.
    /// Falls back to token-based chunking (300-500 tokens, overlap 50).
    /// </summary>
    IReadOnlyList<DocumentChunk> Chunk(string text, ChunkMetadata baseMetadata);
}
