using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models;
using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Models.Response;
using LlamaIntegrationAPI.Services;
using LlamaIntegrationAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using OllamaIntegrationAPI.Services;
using OllamaSharp.Models;
using SharpToken;

namespace LlamaIntegrationAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentController : ControllerBase
    {
        private readonly IOllamaService _llamaService;
        private readonly IDocumentProcessor _documentProcessor;
        private readonly IChunkingService _chunker;
        private readonly IEmbeddingService _embedder;
        private readonly IVectorStoreService _vectorStore;
        private readonly ILogger<DocumentController> _logger;

        private const string LegalCollection = "legal_documents";
        private const int MaxDocChunks = 10;
        private const int LegalTopK = 5;

        public DocumentController(
            IOllamaService llamaService,
            IDocumentProcessor documentProcessor,
            IChunkingService chunker,
            IEmbeddingService embedder,
            IVectorStoreService vectorStore,
            ILogger<DocumentController> logger)
        {
            _llamaService = llamaService;
            _documentProcessor = documentProcessor;
            _chunker = chunker;
            _embedder = embedder;
            _vectorStore = vectorStore;
            _logger = logger;
        }

        [HttpPost("extract-file")]
        public async Task<IActionResult> ExtractFromFile(
            [FromForm] ExtractFromFileRequest request,
            CancellationToken ct)
        {
            if (!LlamaRequestValidation.IsValid(request, out var errorMessage))
                return BadRequest(ResponseHandler.Error(errorMessage));

            // 1. Extract text (reuse existing logic — handles PDF, Word, images, TIFFs)
            var text = await _documentProcessor.ProcessAsync(request).ConfigureAwait(false);

            if (string.IsNullOrEmpty(text))
                return StatusCode(500, ResponseHandler.Error("No se pudo extraer contenido"));

            // 2. Chunk the document instead of sending the full text
            var metadata = new ChunkMetadata
            {
                DocumentName = request.File?.FileName ?? "uploaded",
                DocumentType = request.File?.ContentType ?? "unknown",
                Source = "user-upload"
            };
            var docChunks = _chunker.Chunk(text, metadata);

            _logger.LogInformation(
                "Document chunked into {Count} parts for '{File}'.",
                docChunks.Count, metadata.DocumentName);

            // 3. Keep the original user prompt before we modify it
            var userPrompt = request.Prompt;

            // 4. Select most relevant document chunks (all if small, top-K if large)
            var relevantDocChunks = await RankChunksByRelevance(docChunks, userPrompt, ct);

            // 5. Retrieve legal context from vector store (graceful — no error if empty)
            var legalChunks = await RetrieveLegalContext(userPrompt, ct);

            // 6. Build enriched prompt from relevant chunks only
            request.Prompt = ContextBuilder.Build(userPrompt, relevantDocChunks, legalChunks);

            var tokenCount = GptEncoding.GetEncoding("cl100k_base").CountTokens(request.Prompt);
            request.Stream = false;
            request.Options = new RequestOptions
            {
                Temperature = 0,
                NumCtx = tokenCount + 2000
            };

            _logger.LogInformation(
                "Sending {Tokens} tokens to LLM ({DocChunks} doc chunks + {LegalChunks} legal chunks).",
                tokenCount, relevantDocChunks.Count, legalChunks.Count);

            // 7. Send to LLM
            var result = await _llamaService.ExtractInfoAsync(request).ConfigureAwait(false);

            return StatusCode((int)result.StatusCode, result);
        }

        // ── Private helpers ──────────────────────────────────────────

        /// <summary>
        /// If the document has few chunks, return all of them.
        /// Otherwise, embed the query and every chunk, then pick the
        /// <see cref="MaxDocChunks"/> with highest cosine similarity.
        /// </summary>
        private async Task<IReadOnlyList<DocumentChunk>> RankChunksByRelevance(
            IReadOnlyList<DocumentChunk> chunks,
            string query,
            CancellationToken ct)
        {
            if (chunks.Count <= MaxDocChunks)
                return chunks;

            _logger.LogInformation(
                "Ranking {Total} chunks — selecting top {K} by relevance.", chunks.Count, MaxDocChunks);

            var queryEmbedding = await _embedder.GenerateEmbeddingAsync(query, ct);

            var chunkTexts = chunks.Select(c => c.Text).ToList();
            var chunkEmbeddings = await _embedder.GenerateEmbeddingsAsync(chunkTexts, ct);

            return chunks
                .Select((chunk, i) => (
                    Chunk: chunk,
                    Score: VectorMath.CosineSimilarity(queryEmbedding, chunkEmbeddings[i])))
                .OrderByDescending(x => x.Score)
                .Take(MaxDocChunks)
                .Select(x => x.Chunk)
                .ToList();
        }

        /// <summary>
        /// Tries to retrieve relevant legal/regulatory chunks from Qdrant.
        /// Returns an empty list if the collection doesn't exist yet or any error occurs.
        /// </summary>
        private async Task<IReadOnlyList<DocumentChunk>> RetrieveLegalContext(
            string query, CancellationToken ct)
        {
            try
            {
                var queryEmbedding = await _embedder.GenerateEmbeddingAsync(query, ct);
                return await _vectorStore.SearchAsync(LegalCollection, queryEmbedding, LegalTopK, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex, "No legal context available — vector store may be empty.");
                return [];
            }
        }
    }
}
