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

        if (string.IsNullOrWhiteSpace(request.Model))
            request.Model = "gemma3:4b";

        logger.LogInformation(
            "[QueryController] POST /api/query — Query: {Query} | Model: {Model} | TopK: {TopK}",
            request.Query, request.Model, request.TopK);

        var result = await orchestrator.HandleAsync(
            request.Query,
            request.Model,
            request.ExternalProvider,
            request.ExternalModel,
            topK: request.TopK,
            forceSpanish: request.ForceSpanish,
            reviewWithAi: request.ReviewWithAi,
            ct: ct);

        return StatusCode((int)result.StatusCode, result);
    }

    /// <summary>
    /// Runs a query enriched with context extracted from an uploaded file.
    /// Send the file using the multipart/form-data field named <b>file</b>.
    /// </summary>
    [HttpPost("with-file")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> QueryWithFile(
        [FromForm] QueryRequest request,
        IFormFile? file,
        CancellationToken ct)
    {
        if (file is null)
            return BadRequest(ResponseHandler.Error(
                "No se recibió ningún archivo. Envíe el archivo usando el campo multipart/form-data llamado 'file'."));

        if (file.Length == 0)
            return BadRequest(ResponseHandler.Error("El archivo recibido está vacío."));

        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(ResponseHandler.Error("A query is required."));

        if (string.IsNullOrWhiteSpace(request.Model))
            request.Model = "gemma3:4b";

        logger.LogInformation(
            "[QueryController] POST /api/query/with-file — File: {FileName} | Size: {Size} bytes | ContentType: {ContentType} | Model: {Model} | TopK: {TopK}",
            file.FileName, file.Length, file.ContentType, request.Model, request.TopK);

        var result = await orchestrator.HandleAsync(
            request.Query,
            request.Model,
            request.ExternalProvider,
            request.ExternalModel,
            file,
            request.TopK,
            request.ForceSpanish,
            request.ReviewWithAi,
            ct);

        return StatusCode((int)result.StatusCode, result);
    }
}
