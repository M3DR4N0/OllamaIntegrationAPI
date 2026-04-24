using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Services.Interfaces;

namespace LlamaIntegrationAPI.Services.Implementations;

/// <summary>
/// Contract analysis pipeline:
/// Extract → Chunk (in-memory) → Retrieve legal context → LLM → structured JSON.
/// Contract chunks are NEVER stored in the vector database.
/// </summary>
public class AnalysisService(
    IDocumentParserService parser,
    IChunkingService chunker,
    IEmbeddingService embedder,
    IVectorStoreService vectorStore,
    ILLMService llm,
    ILogger<AnalysisService> logger) : IAnalysisService
{
    private const string LegalCollection = "legal_documents";
    private const int MaxContractChunks = 12;

    // ── System prompt (as specified in requirements) ─────────────────

    private const string SystemPrompt = """
        You are a legal expert in international trade law.

        You are given:
        1. Contract fragments
        2. Relevant legal/regulatory excerpts

        Your task:
        - Determine whether the contract complies with the provided legal/regulatory framework.
        - Identify specific risks, inconsistencies, or missing clauses.
        - Reference the exact articles or sections from the legal excerpts that support your analysis.
        - Provide a concise summary of your findings.

        RULES:
        - Base your analysis ONLY on the provided text. Do NOT invent information.
        - If no legal excerpts are provided, analyze the contract on its own merits and note the absence of regulatory context.
        - Be precise: cite article numbers, section names, and clause references when available.
        - Write in the same language as the contract.

        Return ONLY valid JSON with this exact schema:
        {
          "compliance": boolean,
          "risks": ["string"],
          "related_articles": ["string"],
          "summary": "string"
        }
        """;

    // ── Public API ───────────────────────────────────────────────────

    public async Task<AnalysisResult> AnalyzeContractAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        // 1. Extract text from the uploaded contract
        var contractText = await parser.ExtractTextAsync(request.ContractFile);

        if (string.IsNullOrWhiteSpace(contractText))
            throw new InvalidOperationException("Could not extract text from the contract file.");

        logger.LogInformation(
            "Extracted {Chars} chars from contract '{File}'.",
            contractText.Length, request.ContractFile.FileName);

        // 2. Chunk the contract (in-memory only — not persisted)
        var metadata = new ChunkMetadata
        {
            DocumentName = request.ContractFile.FileName,
            DocumentType = request.ContractFile.ContentType,
            Source = "contract-upload"
        };
        var contractChunks = chunker.Chunk(contractText, metadata);

        logger.LogInformation("Contract split into {Count} chunks.", contractChunks.Count);

        // 3. Select the most relevant contract chunks for the query
        var relevantContractChunks = await RankChunksByRelevance(
            contractChunks, request.Query, MaxContractChunks, ct);

        // 4. Retrieve legal/regulatory context from the vector store
        var legalChunks = await RetrieveLegalContext(request.Query, request.TopK, ct);

        logger.LogInformation(
            "Analysis context: {DocChunks} contract chunks + {LegalChunks} legal chunks.",
            relevantContractChunks.Count, legalChunks.Count);

        // 5. Build the user prompt from combined context
        var userPrompt = ContextBuilder.Build(request.Query, relevantContractChunks, legalChunks);

        // 6. Call LLM with typed deserialization
        var result = await llm.GenerateAsync<AnalysisResult>(SystemPrompt, userPrompt, request.Model, ct);

        if (result is null)
        {
            logger.LogWarning("LLM did not return a valid AnalysisResult — returning fallback.");

            // Fallback: ask again as plain text so we can at least give a summary
            var rawResponse = await llm.GenerateAsync(SystemPrompt, userPrompt, request.Model, ct);
            return new AnalysisResult
            {
                Compliance = false,
                Risks = ["LLM response could not be parsed into structured format."],
                RelatedArticles = [],
                Summary = rawResponse
            };
        }

        return result;
    }

    // ── Private helpers ──────────────────────────────────────────────

    private async Task<IReadOnlyList<DocumentChunk>> RankChunksByRelevance(
        IReadOnlyList<DocumentChunk> chunks,
        string query,
        int maxChunks,
        CancellationToken ct)
    {
        if (chunks.Count <= maxChunks)
            return chunks;

        logger.LogInformation(
            "Ranking {Total} contract chunks — selecting top {K}.", chunks.Count, maxChunks);

        var queryEmbedding = await embedder.GenerateEmbeddingAsync(query, ct);
        var chunkTexts = chunks.Select(c => c.Text).ToList();
        var chunkEmbeddings = await embedder.GenerateEmbeddingsAsync(chunkTexts, ct);

        return chunks
            .Select((chunk, i) => (
                Chunk: chunk,
                Score: VectorMath.CosineSimilarity(queryEmbedding, chunkEmbeddings[i])))
            .OrderByDescending(x => x.Score)
            .Take(maxChunks)
            .Select(x => x.Chunk)
            .ToList();
    }

    private async Task<IReadOnlyList<DocumentChunk>> RetrieveLegalContext(
        string query, int topK, CancellationToken ct)
    {
        try
        {
            var queryEmbedding = await embedder.GenerateEmbeddingAsync(query, ct);
            return await vectorStore.SearchAsync(LegalCollection, queryEmbedding, topK, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No legal context available — vector store may be empty.");
            return [];
        }
    }
}
