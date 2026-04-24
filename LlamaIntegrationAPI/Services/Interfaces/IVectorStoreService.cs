using LlamaIntegrationAPI.Models.Rag;

namespace LlamaIntegrationAPI.Services.Interfaces;

/// <summary>
/// Abstracts the vector database operations (Qdrant).
/// </summary>
public interface IVectorStoreService
{
    Task EnsureCollectionAsync(string collectionName, int vectorSize, CancellationToken ct = default);
    Task UpsertAsync(string collectionName, IEnumerable<DocumentChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentChunk>> SearchAsync(string collectionName, float[] queryVector, int topK = 5, CancellationToken ct = default);
}
