using LlamaIntegrationAPI.Models.Ai;

namespace LlamaIntegrationAPI.Services.Ai;

public interface IAiAnswerReviewService
{
    Task<AiAnswerReviewResult> ReviewAnswerAsync(
        string userRequest,
        string draftAnswer,
        string scenario,
        bool forceSpanish = true,
        string? additionalContext = null,
        string? externalProvider = null,
        string? externalModel = null,
        CancellationToken cancellationToken = default);
}
