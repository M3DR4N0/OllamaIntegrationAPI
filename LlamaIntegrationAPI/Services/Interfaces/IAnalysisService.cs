using LlamaIntegrationAPI.Models.Rag;

namespace LlamaIntegrationAPI.Services.Interfaces;

/// <summary>
/// Performs contract analysis by combining contract chunks with
/// retrieved legal context from the vector store and sending to the LLM.
/// </summary>
public interface IAnalysisService
{
    Task<AnalysisResult> AnalyzeContractAsync(AnalysisRequest request, CancellationToken ct = default);
}
