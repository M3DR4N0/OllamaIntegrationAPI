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

    private static readonly string[] DataKeywords =
        ["total", "sum", "count", "average", "aggregate",
         "how many", "cuántos", "cuánto", "promedio", "suma"];

    // ── System prompts per intent ────────────────────────────────────

    private const string RagSystemPrompt = """
        You are a legal knowledge assistant specialized in international trade law and regulations.

        You are given relevant excerpts from legal documents and regulations.

        Your task:
        - Answer the user's question based ONLY on the provided legal context.
        - Cite specific articles, sections, or clauses when possible.
        - If the context does not contain enough information to answer, say so explicitly.
        - Be precise and concise.
        - Write in the same language as the user's question.
        """;

    private const string GeneralSystemPrompt = """
        You are a helpful assistant with expertise in legal and financial document analysis.

        Answer the user's question clearly and concisely.
        If you are unsure, say so. Do not invent information.
        Write in the same language as the user's question.
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
            QueryIntent.DataQuery        => HandleDataQuery(),
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

        // Data/aggregation keywords → future SQL pipeline
        if (DataKeywords.Any(k => lower.Contains(k)))
            return QueryIntent.DataQuery;

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
            sources = legalChunks.Select(c => new
            {
                document = c.Metadata.DocumentName,
                section = c.Metadata.Section,
                article = c.Metadata.Article
            })
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

        return ResponseHandler.Success(legalChunks.Count > 0
            ? new
            {
                answer = response,
                sources = legalChunks.Select(c => new
                {
                    document = c.Metadata.DocumentName,
                    section = c.Metadata.Section,
                    article = c.Metadata.Article
                })
            }
            : (object)new { answer = response });
    }

    private static IResponse HandleDataQuery()
    {
        return ResponseHandler.Success(
            new { message = "Data/SQL queries are not yet implemented. This feature is planned for a future release." },
            statusCode: System.Net.HttpStatusCode.NotImplemented);
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
        DataQuery,
        General
    }
}
