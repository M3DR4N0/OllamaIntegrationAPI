using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Models.Response;
using LlamaIntegrationAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace LlamaIntegrationAPI.Controllers;

/// <summary>
/// Handles contract analysis against the legal knowledge base.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AnalysisController(
    IAnalysisService analysisService,
    ILogger<AnalysisController> logger) : ControllerBase
{
    [HttpPost("contract")]
    public async Task<IActionResult> AnalyzeContract([FromForm] AnalysisRequest request, CancellationToken ct)
    {
        if (request.ContractFile is null)
            return BadRequest(ResponseHandler.Error("A contract file is required."));

        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(ResponseHandler.Error("A query is required."));

        logger.LogInformation("Analyzing contract: {FileName}", request.ContractFile.FileName);

        var result = await analysisService.AnalyzeContractAsync(request, ct);

        return Ok(ResponseHandler.Success(result));
    }
}
