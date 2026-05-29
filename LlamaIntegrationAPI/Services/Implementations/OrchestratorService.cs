using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Models.Response;
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
    ILogger<OrchestratorService> logger) : IOrchestratorService
{
    private const string LegalCollection = "legal_documents";

    // ── Intent classification keywords ───────────────────────────────

    private static readonly string[] ContractKeywords =
        ["contract", "contrato", "analyze", "analizar", "análisis",
         "compliance", "cumplimiento", "clause", "cláusula", "review"];

    private static readonly string[] LegalKeywords =
        ["law", "ley", "regulation", "regulación", "normativa",
         "article", "artículo", "decreto", "resolution", "resolución",
         "legal", "trade", "comercio", "tariff", "arancel",
         "customs", "aduana", "treaty", "tratado", "statutory"];

    private static readonly string[] DataKeywords = [];

    // ── System prompts per intent ────────────────────────────────────

    private const string RagSystemPrompt = """
        Eres un asistente jurídico especializado en derecho internacional del comercio y regulaciones.

        Se te proporcionan extractos relevantes de documentos legales y normativas.

        Tu tarea:
        - Responde la pregunta del usuario basándote ÚNICAMENTE en el contexto legal proporcionado.
        - Cita artículos, secciones o cláusulas específicas cuando sea posible.
        - Si el contexto no contiene información suficiente para responder, indícalo explícitamente.
        - Sé preciso y conciso.
        - Responde SIEMPRE en español.
        """;

    private const string GeneralSystemPrompt = """
        Eres un asistente experto en análisis de documentos legales y financieros.

        Responde la pregunta del usuario de forma clara y concisa.
        Si no estás seguro, indícalo. No inventes información.
        Responde SIEMPRE en español.
        """;

    // ── Public API ───────────────────────────────────────────────────

    public async Task<IResponse> HandleAsync(
        string query, string model, IFormFile? file = null,
        int topK = 5, CancellationToken ct = default)
    {
        var intent = ClassifyIntent(query, file);
        logger.LogInformation("Query classified as {Intent}: \"{Query}\"", intent, query);

        return intent switch
        {
            QueryIntent.ContractAnalysis => await HandleContractAnalysis(query, model, file, topK, ct),
            QueryIntent.LegalRag         => await HandleLegalRag(query, model, topK, ct),
            _                            => await HandleGeneral(query, model, topK, ct)
        };
    }

    // ── Intent classification (simple rule-based) ────────────────────

    private static QueryIntent ClassifyIntent(string query, IFormFile? file)
    {
        var lower = query.ToLowerInvariant();

        // File + contract keywords → contract analysis
        if (file is not null && ContractKeywords.Any(k => lower.Contains(k)))
            return QueryIntent.ContractAnalysis;

        // File without contract keywords → still route to analysis if file present
        if (file is not null)
            return QueryIntent.ContractAnalysis;

        // Legal/regulatory keywords → RAG
        if (LegalKeywords.Any(k => lower.Contains(k)))
            return QueryIntent.LegalRag;

        // Default: try RAG first (legal context may still be useful), fall back to general
        return QueryIntent.General;
    }

    // ── Pipeline handlers ────────────────────────────────────────────

    private async Task<IResponse> HandleContractAnalysis(
        string query, string model, IFormFile? file, int topK, CancellationToken ct)
    {
        if (file is null)
        {
            logger.LogWarning("Contract analysis requested but no file provided — falling back to RAG.");
            return await HandleLegalRag(query, model, topK, ct);
        }

        var request = new AnalysisRequest
        {
            ContractFile = file,
            Query = query,
            Model = model,
            TopK = topK
        };

        var result = await analysisService.AnalyzeContractAsync(request, ct);
        return ResponseHandler.Success(result);
    }

    private async Task<IResponse> HandleLegalRag(
        string query, string model, int topK, CancellationToken ct)
    {
        // Retrieve relevant legal chunks from vector store
        var legalChunks = await RetrieveLegalContext(query, topK, ct);

        if (legalChunks.Count == 0)
        {
            logger.LogInformation("No legal context found — answering with general knowledge.");
            return await HandleGeneral(query, model, topK, ct);
        }

        // Build context-enriched prompt
        var userPrompt = ContextBuilder.Build(query, [], legalChunks);

        var response = await llm.GenerateAsync(RagSystemPrompt, userPrompt, model, ct);

        return ResponseHandler.Success(new
        {
            answer = response,
            context_used = legalChunks.Count,
            intent = "legal_rag"
        });
    }

    private async Task<IResponse> HandleGeneral(
        string query, string model, int topK, CancellationToken ct)
    {
        // Try to enrich with legal context if available
        var legalChunks = await RetrieveLegalContext(query, topK, ct);

        string userPrompt = legalChunks.Count > 0
            ? ContextBuilder.Build(query, [], legalChunks)
            : query;

        var response = await llm.GenerateAsync(GeneralSystemPrompt, userPrompt, model, ct);

        return ResponseHandler.Success(new
        {
            answer = response,
            context_used = legalChunks.Count,
            intent = "general"
        });
    }

    // ── Shared helpers ───────────────────────────────────────────────

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

    // ── Intent enum ──────────────────────────────────────────────────

    private enum QueryIntent
    {
        LegalRag,
        ContractAnalysis,
        General
    }
}
