using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Services.Interfaces;
using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Services.Implementations;

/// <summary>
/// Two-stage reranking service.
/// Takes coarse-retrieved <see cref="DocumentChunk"/>s and uses the LLM to
/// score each chunk's relevance to the query, returning a sorted list of
/// <see cref="ScoredChunk"/> results.
/// Falls back to embedding-based cosine similarity when the LLM fails.
/// </summary>
public class RerankingService : IRerankingService
{
    private readonly ILLMService _llm;
    private readonly IEmbeddingService _embedding;
    private readonly ILogger<RerankingService> _logger;

    private const int MaxChunksPerLlmCall = 20;

    public RerankingService(
        ILLMService llm,
        IEmbeddingService embedding,
        ILogger<RerankingService> logger)
    {
        _llm = llm;
        _embedding = embedding;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ScoredChunk>> RerankAsync(
        string query,
        IReadOnlyList<DocumentChunk> chunks,
        string model,
        int topN = 5,
        CancellationToken ct = default)
    {
        if (chunks.Count == 0)
            return [];

        if (chunks.Count <= topN)
        {
            return chunks
                .Select(c => new ScoredChunk { Chunk = c, RelevanceScore = 1.0f })
                .ToList();
        }

        // Limit the batch sent to the LLM to avoid context overflow
        var batch = chunks.Count > MaxChunksPerLlmCall
            ? chunks.Take(MaxChunksPerLlmCall).ToList()
            : chunks;

        try
        {
            var scored = await LlmRerankAsync(query, batch, model, ct);

            if (scored is not null && scored.Count > 0)
            {
                _logger.LogInformation(
                    "LLM reranking succeeded — {Scored}/{Total} chunks scored, returning top {TopN}",
                    scored.Count, batch.Count, topN);

                return scored
                    .OrderByDescending(s => s.RelevanceScore)
                    .Take(topN)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM reranking failed, falling back to cosine similarity");
        }

        // Fallback: embedding-based cosine similarity
        return await EmbeddingFallbackAsync(query, batch, topN, ct);
    }

    // ─── LLM reranking ──────────────────────────────────────────────

    private async Task<List<ScoredChunk>?> LlmRerankAsync(
        string query,
        IReadOnlyList<DocumentChunk> chunks,
        string model,
        CancellationToken ct)
    {
        var systemPrompt = BuildRerankSystemPrompt();
        var userPrompt = BuildRerankUserPrompt(query, chunks);

        var response = await _llm.GenerateAsync<RerankLlmResponse>(
            systemPrompt, userPrompt, model, ct);

        if (response?.Rankings is null || response.Rankings.Count == 0)
            return null;

        var chunkById = new Dictionary<int, DocumentChunk>();
        for (int i = 0; i < chunks.Count; i++)
            chunkById[i] = chunks[i];

        var scored = new List<ScoredChunk>();

        foreach (var ranking in response.Rankings)
        {
            if (chunkById.TryGetValue(ranking.Index, out var chunk))
            {
                var clamped = Math.Clamp(ranking.Score, 0f, 1f);
                scored.Add(new ScoredChunk { Chunk = chunk, RelevanceScore = clamped });
            }
        }

        return scored.Count > 0 ? scored : null;
    }

    private static string BuildRerankSystemPrompt() =>
        """
        You are a relevance-scoring engine. You receive a QUERY and a numbered list of text CHUNKS.

        Your task:
        1. Read the QUERY carefully.
        2. For each CHUNK, evaluate how relevant it is to answering the QUERY.
        3. Assign a relevance score between 0.0 (completely irrelevant) and 1.0 (directly answers the query).

        Rules:
        - Score based ONLY on the content provided.
        - Be strict: only give high scores (>0.7) to chunks that directly address the query.
        - Give low scores (<0.3) to chunks that are unrelated or tangentially related.
        - Return ALL chunks with their scores, do not omit any.

        Respond with ONLY a JSON object in this exact format:
        {
          "rankings": [
            { "index": 0, "score": 0.85 },
            { "index": 1, "score": 0.20 }
          ]
        }

        Do NOT include any text outside the JSON object.
        """;

    private static string BuildRerankUserPrompt(string query, IReadOnlyList<DocumentChunk> chunks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"QUERY: {query}");
        sb.AppendLine();
        sb.AppendLine("CHUNKS:");

        for (int i = 0; i < chunks.Count; i++)
        {
            var meta = chunks[i].Metadata;
            var label = !string.IsNullOrEmpty(meta.Article)
                ? meta.Article
                : !string.IsNullOrEmpty(meta.Section) ? meta.Section : $"Chunk {i}";

            // Truncate very long chunks to avoid blowing up context
            var text = chunks[i].Text.Length > 800
                ? chunks[i].Text[..800] + "..."
                : chunks[i].Text;

            sb.AppendLine($"[{i}] ({label})");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ─── Embedding fallback ─────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredChunk>> EmbeddingFallbackAsync(
        string query,
        IReadOnlyList<DocumentChunk> chunks,
        int topN,
        CancellationToken ct)
    {
        _logger.LogInformation("Using embedding cosine-similarity fallback for {Count} chunks", chunks.Count);

        var queryEmbedding = await _embedding.GenerateEmbeddingAsync(query, ct);

        var needEmbedding = chunks.Where(c => c.Embedding is null).Select(c => c.Text).ToList();
        IReadOnlyList<float[]>? generated = null;

        if (needEmbedding.Count > 0)
            generated = await _embedding.GenerateEmbeddingsAsync(needEmbedding, ct);

        int genIdx = 0;
        var scored = new List<ScoredChunk>(chunks.Count);

        foreach (var chunk in chunks)
        {
            var embedding = chunk.Embedding ?? generated?[genIdx++];
            if (embedding is null)
            {
                scored.Add(new ScoredChunk { Chunk = chunk, RelevanceScore = 0f });
                continue;
            }

            var similarity = VectorMath.CosineSimilarity(queryEmbedding, embedding);
            scored.Add(new ScoredChunk { Chunk = chunk, RelevanceScore = similarity });
        }

        return scored
            .OrderByDescending(s => s.RelevanceScore)
            .Take(topN)
            .ToList();
    }

    // ─── LLM response DTOs ─────────────────────────────────────────

    private sealed class RerankLlmResponse
    {
        [JsonPropertyName("rankings")]
        public List<ChunkRanking> Rankings { get; set; } = [];
    }

    private sealed class ChunkRanking
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("score")]
        public float Score { get; set; }
    }
}
