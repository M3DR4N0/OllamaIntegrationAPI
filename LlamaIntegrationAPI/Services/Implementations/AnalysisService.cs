using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models.Ai;
using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Services.Ai;
using LlamaIntegrationAPI.Services.Interfaces;

namespace LlamaIntegrationAPI.Services.Implementations;

/// <summary>
/// Contract analysis pipeline:
/// Extract -> Chunk (in-memory) -> Retrieve legal context -> LLM -> AI review -> final answer.
/// Contract chunks are NEVER stored in the vector database.
/// </summary>
public class AnalysisService(
    IDocumentParserService parser,
    IChunkingService chunker,
    IEmbeddingService embedder,
    IVectorStoreService vectorStore,
    ILLMService llm,
    IAiAnswerReviewService answerReviewService,
    ILogger<AnalysisService> logger) : IAnalysisService
{
    private const string LegalCollection = "legal_documents";
    private const int MaxContractChunks = 12;

    private const string SystemPrompt = """
        Eres un experto legal en derecho internacional del comercio.

        Se te proporcionan:
        1. Fragmentos del contrato
        2. Extractos legales/normativos relevantes (si estan disponibles)

        Responde de forma exhaustiva a la consulta del usuario basandote en el contexto proporcionado.

        REGLAS:
        - Basa tu analisis UNICAMENTE en el texto proporcionado. NO inventes informacion.
        - Si no se proporcionan extractos legales, analiza el contrato por sus propios meritos e indica la ausencia de contexto normativo.
        - Se preciso: cita numeros de articulo, nombres de seccion y referencias de clausula cuando esten disponibles.
        - Responde SIEMPRE en espanol, independientemente del idioma del contrato.
        """;

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
            "[AnalysisService] File received - Name: {FileName} | Size: {Size} bytes | ContentType: {ContentType} | Model: {Model} | TopK: {TopK} | ReviewWithAi: {ReviewWithAi} | ForceSpanish: {ForceSpanish}",
            resolvedFile.FileName,
            resolvedFile.Length,
            resolvedFile.ContentType,
            request.Model,
            request.TopK,
            request.ReviewWithAi,
            request.ForceSpanish);

        var contractText = await parser.ExtractTextAsync(resolvedFile);

        if (string.IsNullOrWhiteSpace(contractText))
        {
            throw new InvalidOperationException(
                $"No se pudo extraer texto del archivo '{resolvedFile.FileName}'. " +
                "El archivo puede estar en blanco, ser un PDF escaneado sin OCR disponible, " +
                "o estar en un formato no compatible.");
        }

        logger.LogInformation(
            "Extracted {Chars} chars from contract '{File}'.",
            contractText.Length,
            resolvedFile.FileName);

        var metadata = new ChunkMetadata
        {
            DocumentName = resolvedFile.FileName,
            DocumentType = resolvedFile.ContentType,
            Source = "contract-upload"
        };
        var contractChunks = chunker.Chunk(contractText, metadata);

        logger.LogInformation("Contract split into {Count} chunks.", contractChunks.Count);

        // Select the most relevant chunks using keyword matching only — no embedding, no Qdrant.
        // Qdrant/embedding is used by other endpoints (query, ingestion), not here.
        var relevantContractChunks = SelectChunksByKeyword(contractChunks, request.Query, MaxContractChunks);

        logger.LogInformation(
            "[AnalysisService] Selected {Selected}/{Total} contract chunks by keyword match.",
            relevantContractChunks.Count, contractChunks.Count);

        var userPrompt = ContextBuilder.Build(request.Query, relevantContractChunks, []);
        var rawResponse = await llm.GenerateAsync(SystemPrompt, userPrompt, request.Model, ct);
        var reviewedAnswer = await FinalizeAnswerAsync(
            request.Query,
            rawResponse ?? string.Empty,
            "single_contract_analysis",
            request.ForceSpanish,
            request.ReviewWithAi,
            request.ExternalProvider,
            request.ExternalModel,
            ct);

        return new AnalysisResult
        {
            Answer = reviewedAnswer.FinalAnswer,
            OllamaAnswer = reviewedAnswer.OllamaAnswer,
            GeminiAnswer = reviewedAnswer.GeminiAnswer
        };
    }

    private async Task<IReadOnlyList<DocumentChunk>> RankChunksByRelevance(
        IReadOnlyList<DocumentChunk> chunks,
        float[] queryEmbedding,
        int maxChunks,
        CancellationToken ct,
        string? queryText = null)
    {
        if (chunks.Count <= maxChunks)
            return chunks;

        var preFiltered = queryText is not null
            ? KeywordPreFilter(chunks, queryText, maxChunks * 3)
            : chunks;

        logger.LogInformation(
            "Ranking {Total} contract chunks - pre-filtered to {PreFiltered}, selecting top {K}.",
            chunks.Count,
            preFiltered.Count,
            maxChunks);

        var chunkTexts = preFiltered.Select(c => c.Text).ToList();
        var chunkEmbeddings = await embedder.GenerateEmbeddingsAsync(chunkTexts, ct);

        return preFiltered
            .Select((chunk, i) => (
                Chunk: chunk,
                Score: VectorMath.CosineSimilarity(queryEmbedding, chunkEmbeddings[i])))
            .OrderByDescending(x => x.Score)
            .Take(maxChunks)
            .Select(x => x.Chunk)
            .ToList();
    }

    /// <summary>
    /// Selects the most relevant chunks for the contract analysis pipeline using
    /// keyword matching only — no embeddings, no Qdrant, no HTTP calls.
    /// If the document has fewer chunks than <paramref name="max"/>, all are returned.
    /// </summary>
    private static IReadOnlyList<DocumentChunk> SelectChunksByKeyword(
        IReadOnlyList<DocumentChunk> chunks,
        string query,
        int max)
    {
        if (chunks.Count <= max)
            return chunks;

        return KeywordPreFilter(chunks, query, max);
    }

    private static IReadOnlyList<DocumentChunk> KeywordPreFilter(
        IReadOnlyList<DocumentChunk> chunks,
        string query,
        int limit)
    {
        var tokens = query
            .ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '?', '!', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3)
            .ToHashSet();

        if (tokens.Count == 0)
            return chunks;

        var scored = chunks
            .Select(c => (
                Chunk: c,
                Score: tokens.Count(t => c.Text.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Score)
            .ToList();

        var candidates = scored.Take(limit).Select(x => x.Chunk).ToList();

        if (candidates.Count < limit)
            return chunks;

        return candidates;
    }

    private async Task<IReadOnlyList<DocumentChunk>> RetrieveLegalContext(
        float[] queryEmbedding,
        int topK,
        CancellationToken ct)
    {
        try
        {
            return await vectorStore.SearchAsync(LegalCollection, queryEmbedding, topK, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No legal context available - vector store may be empty.");
            return [];
        }
    }

    public async Task<AnalysisResult> AnalyzeMultipleDocumentsAsync(
        MultiDocumentAnalysisRequest request,
        CancellationToken ct = default)
    {
        if (request.Files.Count == 0)
            throw new InvalidOperationException("Se requiere al menos un archivo.");

        var allChunks = new List<DocumentChunk>();

        foreach (var file in request.Files)
        {
            var text = await parser.ExtractTextAsync(file);

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("[MultiDoc] No se pudo extraer texto de '{File}' - se omite.", file.FileName);
                continue;
            }

            logger.LogInformation("[MultiDoc] '{File}' -> {Chars} chars.", file.FileName, text.Length);

            var metadata = new ChunkMetadata
            {
                DocumentName = file.FileName,
                DocumentType = file.ContentType,
                Source = "multi-doc-upload"
            };

            allChunks.AddRange(chunker.Chunk(text, metadata));
        }

        if (allChunks.Count == 0)
        {
            throw new InvalidOperationException(
                "No se pudo extraer texto de ninguno de los archivos proporcionados.");
        }

        logger.LogInformation("[MultiDoc] Total de chunks combinados: {Count}.", allChunks.Count);

        var queryEmbedding = await embedder.GenerateEmbeddingAsync(request.Query, ct);

        var relevantChunks = await RankChunksByRelevance(
            allChunks,
            queryEmbedding,
            MaxContractChunks,
            ct,
            request.Query);

        var legalChunks = await RetrieveLegalContext(queryEmbedding, request.TopK, ct);
        var userPrompt = ContextBuilder.Build(request.Query, relevantChunks, legalChunks);
        var rawResponse = await llm.GenerateAsync(SystemPrompt, userPrompt, request.Model, ct);
        var reviewedAnswer = await FinalizeAnswerAsync(
            request.Query,
            rawResponse ?? string.Empty,
            "multi_document_analysis",
            request.ForceSpanish,
            request.ReviewWithAi,
            request.ExternalProvider,
            request.ExternalModel,
            ct);

        return new AnalysisResult
        {
            Answer = reviewedAnswer.FinalAnswer,
            OllamaAnswer = reviewedAnswer.OllamaAnswer,
            GeminiAnswer = reviewedAnswer.GeminiAnswer
        };
    }

    private async Task<AiAnswerReviewResult> FinalizeAnswerAsync(
        string query,
        string rawAnswer,
        string scenario,
        bool forceSpanish,
        bool reviewWithAi,
        string? externalProvider,
        string? externalModel,
        CancellationToken ct)
    {
        if (!reviewWithAi)
        {
            return new AiAnswerReviewResult
            {
                FinalAnswer = rawAnswer,
                OllamaAnswer = rawAnswer
            };
        }

        return await answerReviewService.ReviewAnswerAsync(
            query,
            rawAnswer,
            scenario,
            forceSpanish,
            "Review the contract analysis and ensure the final response matches the requested format and language.",
            externalProvider,
            externalModel,
            ct);
    }
}
