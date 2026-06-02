using LlamaIntegrationAPI.Models.Ai;

namespace LlamaIntegrationAPI.Services.Ai;

public interface IAiGatewayService
{
    Task<AiGenerateResponse> GenerateAsync(AiGenerateRequest request, CancellationToken cancellationToken = default);
}
