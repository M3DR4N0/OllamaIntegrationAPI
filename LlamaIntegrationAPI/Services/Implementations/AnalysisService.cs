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
        Eres un experto legal en derecho internacional del comercio.

        Se te proporcionan:
        1. Fragmentos del contrato
        2. Extractos legales/normativos relevantes (si están disponibles)

        Responde de forma exhaustiva a la consulta del usuario basándote en el contexto proporcionado.

        REGLAS:
        - Basa tu análisis ÚNICAMENTE en el texto proporcionado. NO inventes información.
        - Si no se proporcionan extractos legales, analiza el contrato por sus propios méritos e indica la ausencia de contexto normativo.
        - Sé preciso: cita números de artículo, nombres de sección y referencias de cláusula cuando estén disponibles.
        - Responde SIEMPRE en español, independientemente del idioma del contrato.
        """;

    // ── Public API ───────────────────────────────────────────────────

    public async Task<AnalysisResult> AnalyzeContractAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        var resolvedFile = request.ResolvedFile
            ?? throw new InvalidOperationException("No file was provided in the request.");

#pragma warning disable CS0618
        if (request.ContractFile is not null && request.File is null)
            logger.LogWarning(
                "[LEGACY] Field 'contractFile' received. Please migrate to the standard field 'file'.");
#pragma warning restore CS0618

        logger.LogInformation(
            "[AnalysisService] File received — Name: {FileName} | Size: {Size} bytes | ContentType: {ContentType} | Model: {Model} | TopK: {TopK}",
            resolvedFile.FileName, resolvedFile.Length, resolvedFile.ContentType, request.Model, request.TopK);

        // 1. Extract text from the uploaded contract
        var contractText = await parser.ExtractTextAsync(resolvedFile);

        if (string.IsNullOrWhiteSpace(contractText))
            throw new InvalidOperationException("Could not extract text from the contract file.");

        logger.LogInformation(
            "Extracted {Chars} chars from contract '{File}'.",
            contractText.Length, resolvedFile.FileName);

        // 2. Chunk the contract (in-memory only — not persisted)
        var metadata = new ChunkMetadata
        {
            DocumentName = resolvedFile.FileName,
            DocumentType = resolvedFile.ContentType,
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

        // 6. Call LLM and return plain answer
        var rawResponse = await llm.GenerateAsync(SystemPrompt, userPrompt, request.Model, ct);

        return new AnalysisResult { Answer = rawResponse ?? string.Empty };
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
