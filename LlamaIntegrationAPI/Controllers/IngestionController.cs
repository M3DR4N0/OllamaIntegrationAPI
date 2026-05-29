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

    [HttpPost("upload")]
    public async Task<IActionResult> IngestDocument([FromForm] IngestionRequest request, CancellationToken ct)
    {
        if (request.File is null)
            return BadRequest(ResponseHandler.Error("A file is required for ingestion."));

        if (string.IsNullOrWhiteSpace(request.DocumentType))
            return BadRequest(ResponseHandler.Error("DocumentType is required."));

        if (string.IsNullOrWhiteSpace(request.Source))
            return BadRequest(ResponseHandler.Error("Source is required."));

        logger.LogInformation("Ingesting document: {FileName}", request.File.FileName);

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
