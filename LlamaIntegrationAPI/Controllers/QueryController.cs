using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Models.Response;
using LlamaIntegrationAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace LlamaIntegrationAPI.Controllers;

/// <summary>
/// Handles RAG-based queries against the legal knowledge base.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class QueryController(
    IOrchestratorService orchestrator,
    ILogger<QueryController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Query([FromBody] QueryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(ResponseHandler.Error("A query is required."));

        logger.LogInformation("Processing query: {Query}", request.Query);

        var result = await orchestrator.HandleAsync(request.Query, request.Model, topK: request.TopK, ct: ct);

        return StatusCode((int)result.StatusCode, result);
    }

    [HttpPost("with-file")]
    public async Task<IActionResult> QueryWithFile([FromForm] QueryRequest request, IFormFile? file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(ResponseHandler.Error("A query is required."));

        logger.LogInformation("Processing query with file: {Query}", request.Query);

        var result = await orchestrator.HandleAsync(request.Query, request.Model, file, request.TopK, ct);

        return StatusCode((int)result.StatusCode, result);
    }
}
