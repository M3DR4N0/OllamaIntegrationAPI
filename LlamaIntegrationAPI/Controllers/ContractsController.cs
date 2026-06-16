using LlamaIntegrationAPI.Models.Contracts;
using LlamaIntegrationAPI.Models.Response;
using LlamaIntegrationAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace LlamaIntegrationAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ContractsController(
    IContractMergeService contractMergeService,
    ILogger<ContractsController> logger) : ControllerBase
{
    /// <summary>
    /// Merges two or more contract documents into a consolidated draft.
    /// The first file is treated as the base contract by default.
    /// Send each file using multipart/form-data with the field name <b>files</b>.
    /// </summary>
    /// <remarks>
    /// Example curl:
    ///
    ///     curl -X POST "http://localhost:5000/api/contracts/merge" \
    ///       -F "files=@C:\Temp\borrador-base.pdf" \
    ///       -F "files=@C:\Temp\clausulas-adicionales.pdf" \
    ///       -F "query=Actua como un Abogado Experto en Redaccion de Contratos y Revisor Legal. Tu objetivo es integrar clausulas especificas de manera organica dentro de un borrador de contrato existente." \
    ///       -F "model=gemma3:1b"
    /// </remarks>
    [HttpPost("merge")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> MergeContracts([FromForm] ContractMergeRequest request, CancellationToken ct)
    {
        var validationError = ValidateMergeRequest(request);
        if (validationError is not null)
            return validationError;

        try
        {
            var result = await contractMergeService.MergeContractsAsync(request, ct);
            return Ok(ResponseHandler.Success(result));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[ContractsController] Error fusionando contratos.");
            return UnprocessableEntity(ResponseHandler.Error(ex.Message, System.Net.HttpStatusCode.UnprocessableEntity));
        }
    }

    /// <summary>
    /// Merges two or more contract documents and returns the generated Microsoft Word file directly.
    /// Send each file using multipart/form-data with the field name <b>files</b>.
    /// </summary>
    [HttpPost("merge/docx")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> MergeContractsAsDocx([FromForm] ContractMergeRequest request, CancellationToken ct)
    {
        var validationError = ValidateMergeRequest(request);
        if (validationError is not null)
            return validationError;

        try
        {
            var result = await contractMergeService.MergeContractsAsync(request, ct);

            if (result.WordDocument is null || result.WordDocument.Length == 0)
            {
                logger.LogWarning(
                    "[ContractsController] El merge finalizo pero no genero el archivo DOCX esperado.");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ResponseHandler.Error("El merge finalizo pero no se pudo generar el archivo Word."));
            }

            return File(
                result.WordDocument,
                result.WordDocumentContentType ?? "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                result.WordDocumentFileName ?? "merged.docx");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[ContractsController] Error fusionando contratos.");
            return UnprocessableEntity(ResponseHandler.Error(ex.Message, System.Net.HttpStatusCode.UnprocessableEntity));
        }
    }

    private IActionResult? ValidateMergeRequest(ContractMergeRequest request)
    {
        if (request.Files is null || request.Files.Count < 2)
        {
            return BadRequest(ResponseHandler.Error(
                "Se requieren al menos dos archivos. Envielos usando el campo multipart/form-data llamado 'files'."));
        }

        if (request.Files.Any(file => file.Length == 0))
            return BadRequest(ResponseHandler.Error("Uno o mas archivos recibidos estan vacios."));

        if (request.BaseDocumentIndex < 0 || request.BaseDocumentIndex >= request.Files.Count)
        {
            return BadRequest(ResponseHandler.Error(
                "BaseDocumentIndex debe apuntar a uno de los archivos enviados."));
        }

#pragma warning disable CS0618
        if (string.IsNullOrWhiteSpace(request.Query) && !string.IsNullOrWhiteSpace(request.Prompt))
            request.Query = request.Prompt;
#pragma warning restore CS0618

        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(ResponseHandler.Error("La consulta (query) es requerida."));

        if (string.IsNullOrWhiteSpace(request.Model))
            request.Model = "gemma3:1b";

        if (string.IsNullOrWhiteSpace(request.Query))
            request.Query = ContractMergeRequest.DefaultQuery;

        logger.LogInformation(
            "[ContractsController] POST /api/contracts/merge - {Count} archivo(s) | BaseDocumentIndex: {BaseDocumentIndex} | Model: {Model}",
            request.Files.Count,
            request.BaseDocumentIndex,
            request.Model);

        return null;
    }
}
