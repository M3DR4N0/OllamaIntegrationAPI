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
    /// <summary>
    /// Analyzes a contract document using RAG. Send the file using the multipart/form-data field named <b>file</b>.
    /// </summary>
    /// <remarks>
    /// Example curl:
    ///
    ///     curl -X POST "http://localhost:5000/api/analysis/contract" \
    ///       -F "file=@C:\Temp\contrato.pdf" \
    ///       -F "query=Analiza las obligaciones principales del contrato" \
    ///       -F "model=gemma3:1b" \
    ///       -F "topK=8"
    ///
    /// Example PowerShell:
    ///
    ///     $form = @{
    ///         file  = Get-Item "C:\Temp\contrato.pdf"
    ///         query = "Analiza las obligaciones principales del contrato"
    ///         model = "gemma3:1b"
    ///         topK  = "8"
    ///     }
    ///     Invoke-RestMethod -Uri "http://localhost:5000/api/analysis/contract" -Method Post -Form $form
    /// </remarks>
    [HttpPost("contract")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AnalyzeContract([FromForm] AnalysisRequest request, CancellationToken ct)
    {
        // Legacy field detection
#pragma warning disable CS0618
        if (request.ContractFile is not null && request.File is null)
        {
            logger.LogWarning(
                "[LEGACY] Field 'contractFile' received on POST /api/analysis/contract. " +
                "Please migrate to the standard field 'file'.");
            request.File = request.ContractFile;
        }
#pragma warning restore CS0618

        if (request.ResolvedFile is null)
            return BadRequest(ResponseHandler.Error(
                "No se recibió ningún archivo. Envíe el archivo usando el campo multipart/form-data llamado 'file'."));

        if (request.ResolvedFile.Length == 0)
            return BadRequest(ResponseHandler.Error("El archivo recibido está vacío."));

        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(ResponseHandler.Error("A query is required."));

        if (string.IsNullOrWhiteSpace(request.Model))
            request.Model = "gemma3:1b";

        logger.LogInformation(
            "[AnalysisController] POST /api/analysis/contract — File: {FileName} | Size: {Size} bytes | ContentType: {ContentType} | Model: {Model} | TopK: {TopK}",
            request.ResolvedFile.FileName, request.ResolvedFile.Length,
            request.ResolvedFile.ContentType, request.Model, request.TopK);

        var result = await analysisService.AnalyzeContractAsync(request, ct);

        return Ok(ResponseHandler.Success(result));
    }
}
