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
    IDocumentOutputService documentOutputService,
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
    ///       -F "model=gemma3:4b" \
    ///       -F "topK=8"
    ///
    /// Example PowerShell:
    ///
    ///     $form = @{
    ///         file  = Get-Item "C:\Temp\contrato.pdf"
    ///         query = "Analiza las obligaciones principales del contrato"
    ///         model = "gemma3:4b"
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
            request.Model = "gemma3:4b";

        logger.LogInformation(
            "[AnalysisController] POST /api/analysis/contract — File: {FileName} | Size: {Size} bytes | ContentType: {ContentType} | Model: {Model} | TopK: {TopK}",
            request.ResolvedFile.FileName, request.ResolvedFile.Length,
            request.ResolvedFile.ContentType, request.Model, request.TopK);

        try
        {
            var result = await analysisService.AnalyzeContractAsync(request, ct);

            if (request.ResolvedOutputFormat == Models.Documents.DocumentOutputFormat.Docx)
            {
                var generatedDocument = documentOutputService.CreateWordDocument(
                    result.Answer,
                    $"{Path.GetFileNameWithoutExtension(request.ResolvedFile.FileName)}-analysis");

                return File(
                    generatedDocument.Content,
                    generatedDocument.ContentType,
                    generatedDocument.FileName);
            }

            if (request.ResolvedOutputFormat == Models.Documents.DocumentOutputFormat.Both)
            {
                var generatedDocument = documentOutputService.CreateWordDocument(
                    result.Answer,
                    $"{Path.GetFileNameWithoutExtension(request.ResolvedFile.FileName)}-analysis");

                result = result with
                {
                    AnswerFormat = "markdown",
                    WordDocument = generatedDocument.Content,
                    WordDocumentFileName = generatedDocument.FileName,
                    WordDocumentContentType = generatedDocument.ContentType
                };
            }

            return Ok(ResponseHandler.Success(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No se pudo extraer texto"))
        {
            logger.LogWarning(ex, "[AnalysisController] No se pudo extraer texto del archivo.");
            return UnprocessableEntity(ResponseHandler.Error(ex.Message, System.Net.HttpStatusCode.UnprocessableEntity));
        }
    }

    /// <summary>
    /// Analyzes two or more documents together against the legal knowledge base.
    /// Send each file using multipart/form-data with the field name <b>files</b>.
    /// </summary>
    /// <remarks>
    /// Example curl:
    ///
    ///     curl -X POST "http://localhost:5000/api/analysis/multi" \
    ///       -F "files=@C:\Temp\contrato1.pdf" \
    ///       -F "files=@C:\Temp\contrato2.pdf" \
    ///       -F "query=Compara las obligaciones de ambos contratos" \
    ///       -F "model=gemma3:4b" \
    ///       -F "topK=8"
    /// </remarks>
    [HttpPost("multi")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AnalyzeMultipleDocuments(
        [FromForm] MultiDocumentAnalysisRequest request, CancellationToken ct)
    {
        if (request.Files is null || request.Files.Count == 0)
            return BadRequest(ResponseHandler.Error(
                "Se requieren al menos dos archivos. Envíelos usando el campo multipart/form-data llamado 'files'."));

        if (request.Files.Any(f => f.Length == 0))
            return BadRequest(ResponseHandler.Error("Uno o más archivos recibidos están vacíos."));

        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(ResponseHandler.Error("La consulta (query) es requerida."));

        if (string.IsNullOrWhiteSpace(request.Model))
            request.Model = "gemma3:4b";

        logger.LogInformation(
            "[AnalysisController] POST /api/analysis/multi — {Count} archivo(s) | Model: {Model} | TopK: {TopK}",
            request.Files.Count, request.Model, request.TopK);

        try
        {
            var result = await analysisService.AnalyzeMultipleDocumentsAsync(request, ct);
            return Ok(ResponseHandler.Success(result));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[AnalysisController] Error procesando múltiples documentos.");
            return UnprocessableEntity(ResponseHandler.Error(ex.Message, System.Net.HttpStatusCode.UnprocessableEntity));
        }
    }
}
