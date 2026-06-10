using LlamaIntegrationAPI.Models.Contracts;

namespace LlamaIntegrationAPI.Services.Interfaces;

public interface IContractMergeService
{
    Task<ContractMergeResult> MergeContractsAsync(ContractMergeRequest request, CancellationToken ct = default);
}
