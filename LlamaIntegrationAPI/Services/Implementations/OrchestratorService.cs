using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models.Ai;
using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Models.Response;
using LlamaIntegrationAPI.Services.Ai;
using LlamaIntegrationAPI.Services.Interfaces;

namespace LlamaIntegrationAPI.Services.Implementations;

/// <summary>
/// Simple rule-based orchestrator that classifies user intent via keyword
/// matching and routes to the appropriate pipeline.
/// </summary>
public class OrchestratorService(
    IEmbeddingService embedder,
    IVectorStoreService vectorStore,
    ILLMService llm,
    IAnalysisService analysisService,
    IAiAnswerReviewService answerReviewService,
    ILogger<OrchestratorService> logger) : IOrchestratorService
{
    private const string LegalCollection = "legal_documents";

    private static readonly string[] ContractKeywords =
        ["contract", "contrato", "analyze", "analizar", "analisis",
         "compliance", "cumplimiento", "clause", "clausula", "review"];

    private static readonly string[] LegalKeywords =
        ["law", "ley", "regulation", "regulacion", "normativa",
         "article", "articulo", "decreto", "resolution", "resolucion",
         "legal", "trade", "comercio", "tariff", "arancel",
         "customs", "aduana", "treaty", "tratado", "statutory"];

    private const string RagSystemPrompt = """
        Eres un asistente juridico especializado en derecho internacional del comercio y regulaciones.

        Se te proporcionan extractos relevantes de documentos legales y normativas.

        Tu tarea:
        - Responde la pregunta del usuario basandote UNICAMENTE en el contexto legal proporcionado.
        - Cita articulos, secciones o clausulas especificas cuando sea posible.
        - Si el contexto no contiene informacion suficiente para responder, indicalo explicitamente.
        - Se preciso y conciso.
        - Responde SIEMPRE en espanol.
        """;

    private const string GeneralSystemPrompt = """
        Eres un asistente experto en analisis de documentos legales y financieros.

        Responde la pregunta del usuario de forma clara y concisa.
        Si no estas seguro, indicalo. No inventes informacion.
        Responde SIEMPRE en espanol.
        """;

    public async Task<IResponse> HandleAsync(
        string query,
        string model,
        IFormFile? file = null,
        int topK = 5,
        bool forceSpanish = true,
        bool reviewWithAi = true,
        CancellationToken ct = default)
    {
        var intent = ClassifyIntent(query, file);
        logger.LogInformation(
            "Query classified as {Intent}: \"{Query}\" | ForceSpanish: {ForceSpanish} | ReviewWithAi: {ReviewWithAi}",
            intent,
            query,
            forceSpanish,
            reviewWithAi);

        return intent switch
        {
            QueryIntent.ContractAnalysis => await HandleContractAnalysis(query, model, file, topK, forceSpanish, reviewWithAi, ct),
            QueryIntent.LegalRag => await HandleLegalRag(query, model, topK, forceSpanish, reviewWithAi, ct),
            _ => await HandleGeneral(query, model, topK, forceSpanish, reviewWithAi, ct)
        };
    }

    private static QueryIntent ClassifyIntent(string query, IFormFile? file)
    {
        var lower = query.ToLowerInvariant();

        if (file is not null && ContractKeywords.Any(k => lower.Contains(k)))
            return QueryIntent.ContractAnalysis;

        if (file is not null)
            return QueryIntent.ContractAnalysis;

        if (LegalKeywords.Any(k => lower.Contains(k)))
            return QueryIntent.LegalRag;

        return QueryIntent.General;
    }

    private async Task<IResponse> HandleContractAnalysis(
        string query,
        string model,
        IFormFile? file,
        int topK,
        bool forceSpanish,
        bool reviewWithAi,
        CancellationToken ct)
    {
        if (file is null)
        {
            logger.LogWarning("Contract analysis requested but no file provided - falling back to RAG.");
            return await HandleLegalRag(query, model, topK, forceSpanish, reviewWithAi, ct);
        }

        var request = new AnalysisRequest
        {
            ContractFile = file,
            Query = query,
            Model = model,
            TopK = topK,
            ForceSpanish = forceSpanish,
            ReviewWithAi = reviewWithAi
        };

        var result = await analysisService.AnalyzeContractAsync(request, ct);
        return ResponseHandler.Success(result);
    }

    private async Task<IResponse> HandleLegalRag(
        string query,
        string model,
        int topK,
        bool forceSpanish,
        bool reviewWithAi,
        CancellationToken ct)
    {
        var legalChunks = await RetrieveLegalContext(query, topK, ct);

        if (legalChunks.Count == 0)
        {
            logger.LogInformation("No legal context found - answering with general knowledge.");
            return await HandleGeneral(query, model, topK, forceSpanish, reviewWithAi, ct);
        }

        var userPrompt = ContextBuilder.Build(query, [], legalChunks);
        var response = await llm.GenerateAsync(RagSystemPrompt, userPrompt, model, ct);
        var reviewedAnswer = await FinalizeAnswerAsync(
            query,
            response,
            "legal_rag_query",
            forceSpanish,
            reviewWithAi,
            "Validate that the final answer uses only the retrieved legal context and follows the requested language and format.",
            ct);

        return ResponseHandler.Success(new QueryAnswerResult
        {
            Answer = reviewedAnswer.FinalAnswer,
            OllamaAnswer = reviewedAnswer.OllamaAnswer,
            GeminiAnswer = reviewedAnswer.GeminiAnswer,
            ContextUsed = legalChunks.Count,
            Intent = "legal_rag"
        });
    }

    private async Task<IResponse> HandleGeneral(
        string query,
        string model,
        int topK,
        bool forceSpanish,
        bool reviewWithAi,
        CancellationToken ct)
    {
        var legalChunks = await RetrieveLegalContext(query, topK, ct);

        var userPrompt = legalChunks.Count > 0
            ? ContextBuilder.Build(query, [], legalChunks)
            : query;

        var response = await llm.GenerateAsync(GeneralSystemPrompt, userPrompt, model, ct);
        var reviewedAnswer = await FinalizeAnswerAsync(
            query,
            response,
            "general_query",
            forceSpanish,
            reviewWithAi,
            "Validate that the response answers the user request clearly and in the expected language.",
            ct);

        return ResponseHandler.Success(new QueryAnswerResult
        {
            Answer = reviewedAnswer.FinalAnswer,
            OllamaAnswer = reviewedAnswer.OllamaAnswer,
            GeminiAnswer = reviewedAnswer.GeminiAnswer,
            ContextUsed = legalChunks.Count,
            Intent = "general"
        });
    }

    private async Task<IReadOnlyList<DocumentChunk>> RetrieveLegalContext(
        string query,
        int topK,
        CancellationToken ct)
    {
        try
        {
            var queryEmbedding = await embedder.GenerateEmbeddingAsync(query, ct);
            return await vectorStore.SearchAsync(LegalCollection, queryEmbedding, topK, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No legal context available - vector store may be empty.");
            return [];
        }
    }

    private async Task<AiAnswerReviewResult> FinalizeAnswerAsync(
        string query,
        string rawAnswer,
        string scenario,
        bool forceSpanish,
        bool reviewWithAi,
        string additionalContext,
        CancellationToken ct)
    {
        if (!reviewWithAi || string.IsNullOrWhiteSpace(rawAnswer))
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
            additionalContext,
            ct);
    }

    private enum QueryIntent
    {
        LegalRag,
        ContractAnalysis,
        General
    }
}
