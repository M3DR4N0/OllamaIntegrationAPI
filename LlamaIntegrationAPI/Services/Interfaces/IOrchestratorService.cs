using LlamaIntegrationAPI.Models.Response;

namespace LlamaIntegrationAPI.Services.Interfaces;

/// <summary>
/// Simple rule-based orchestrator that routes user queries to the appropriate pipeline.
/// </summary>
public interface IOrchestratorService
{
    /// <summary>
    /// Determines the intent from the query and routes to the correct service
    /// (RAG for legal queries, analysis for contract queries, etc.).
    /// </summary>
    Task<IResponse> HandleAsync(string query, string model, IFormFile? file = null, int topK = 5, CancellationToken ct = default);
}
