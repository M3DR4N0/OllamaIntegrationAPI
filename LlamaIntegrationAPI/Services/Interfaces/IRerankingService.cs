using LlamaIntegrationAPI.Models.Rag;

namespace LlamaIntegrationAPI.Services.Interfaces;

/// <summary>
/// Two-stage retrieval: takes coarse-retrieved chunks and uses an LLM
/// to rerank them by relevance to the query, returning the top-N most
/// relevant chunks with a confidence score.
/// </summary>
public interface IRerankingService
{
    /// <summary>
    /// Reranks <paramref name="chunks"/> by relevance to <paramref name="query"/>
    /// using an LLM and returns the top <paramref name="topN"/> results.
    /// Falls back to embedding-based cosine similarity ranking when the LLM
    /// reranking fails.
    /// </summary>
    Task<IReadOnlyList<ScoredChunk>> RerankAsync(
        string query,
        IReadOnlyList<DocumentChunk> chunks,
        string model,
        int topN = 5,
        CancellationToken ct = default);
}
