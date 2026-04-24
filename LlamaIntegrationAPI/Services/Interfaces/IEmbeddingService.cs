namespace LlamaIntegrationAPI.Services.Interfaces;

/// <summary>
/// Generates embedding vectors for text chunks using a local or API-based model.
/// </summary>
public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default);
    int Dimensions { get; }
}
