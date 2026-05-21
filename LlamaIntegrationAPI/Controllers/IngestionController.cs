using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Models.Response;
using LlamaIntegrationAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace LlamaIntegrationAPI.Controllers;

/// <summary>
/// Handles ingestion of legal/regulatory documents into the vector store.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class IngestionController(
    IDocumentParserService parser,
    IChunkingService chunker,
    IEmbeddingService embedder,
    IVectorStoreService vectorStore,
    ILogger<IngestionController> logger) : ControllerBase
{
    private const string DefaultCollection = "legal_documents";

    /// <summary>
    /// Ingests a document into the vector store. Send the file using the multipart/form-data field named <b>file</b>.
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> IngestDocument([FromForm] IngestionRequest request, CancellationToken ct)
    {
        if (request.File is null)
            return BadRequest(ResponseHandler.Error(
                "No se recibió ningún archivo. Envíe el archivo usando el campo multipart/form-data llamado 'file'."));

        if (request.File.Length == 0)
            return BadRequest(ResponseHandler.Error("El archivo recibido está vacío."));

        if (string.IsNullOrWhiteSpace(request.DocumentType))
            return BadRequest(ResponseHandler.Error("DocumentType is required."));

        if (string.IsNullOrWhiteSpace(request.Source))
            return BadRequest(ResponseHandler.Error("Source is required."));

        logger.LogInformation(
            "[IngestionController] POST /api/ingestion/upload — File: {FileName} | Size: {Size} bytes | ContentType: {ContentType}",
            request.File.FileName, request.File.Length, request.File.ContentType);

        var text = await parser.ExtractTextAsync(request.File);

        if (string.IsNullOrWhiteSpace(text))
            return StatusCode(500, ResponseHandler.Error("No text could be extracted from the document."));

        var metadata = new ChunkMetadata
        {
            DocumentName = request.File.FileName,
            DocumentType = request.DocumentType,
            Source = request.Source
        };

        var chunks = chunker.Chunk(text, metadata);

        await vectorStore.EnsureCollectionAsync(DefaultCollection, embedder.Dimensions, ct);

        var texts = chunks.Select(c => c.Text).ToList();
        var embeddings = await embedder.GenerateEmbeddingsAsync(texts, ct);

        var enrichedChunks = chunks.Select((c, i) => c with { Embedding = embeddings[i] }).ToList();

        await vectorStore.UpsertAsync(DefaultCollection, enrichedChunks, ct);

        logger.LogInformation("Ingested {Count} chunks from {FileName}", enrichedChunks.Count, request.File.FileName);

        return Ok(ResponseHandler.Success(new { chunksIngested = enrichedChunks.Count }));
    }
}
