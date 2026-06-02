using LlamaIntegrationAPI.Models.Ai;

namespace LlamaIntegrationAPI.Services.Ai;

public interface IAiProvider
{
    string ProviderName { get; }
    string ModelName { get; }
    Task<AiGenerateResponse> GenerateAsync(AiGenerateRequest request, CancellationToken cancellationToken);
}
