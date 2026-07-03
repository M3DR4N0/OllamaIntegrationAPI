using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models.Documents;
using LlamaIntegrationAPI.Models;
using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Models.Response;
using LlamaIntegrationAPI.Services;
using LlamaIntegrationAPI.Services.Ai;
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
        private readonly IAiAnswerReviewService _answerReviewService;
        private readonly ILogger<DocumentController> _logger;
        private readonly int _maxNumCtx;
        private readonly int _responseBuffer;

        private const string LegalCollection = "legal_documents";
        private const int MaxDocChunks = 10;
        private const int LegalTopK = 5;

        public DocumentController(
            IOllamaService llamaService,
            IDocumentProcessor documentProcessor,
            IChunkingService chunker,
            IEmbeddingService embedder,
            IVectorStoreService vectorStore,
            IAiAnswerReviewService answerReviewService,
            IConfiguration configuration,
            ILogger<DocumentController> logger)
        {
            _llamaService = llamaService;
            _documentProcessor = documentProcessor;
            _chunker = chunker;
            _embedder = embedder;
            _vectorStore = vectorStore;
            _answerReviewService = answerReviewService;
            _logger = logger;
            _maxNumCtx = int.TryParse(configuration["LLM_MAX_NUM_CTX"], out var maxNumCtx) && maxNumCtx > 0
                ? maxNumCtx
                : 8192;
            _responseBuffer = int.TryParse(configuration["LLM_RESPONSE_BUFFER"], out var responseBuffer) && responseBuffer > 0
                ? responseBuffer
                : 512;
        }

        [HttpPost("to-base64")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> ConvertToBase64(
            [FromForm] DocumentToBase64Request request,
            CancellationToken ct)
        {
            if (request.File is null)
                return BadRequest(ResponseHandler.Error(
                    "No se recibio ningun archivo. Envie el archivo usando el campo multipart/form-data llamado 'file'."));

            if (request.File.Length == 0)
                return BadRequest(ResponseHandler.Error("El archivo recibido esta vacio."));

            await using var stream = request.File.OpenReadStream();
            await using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, ct);

            var response = new DocumentToBase64Response
            {
                FileName = request.File.FileName,
                Base64 = Convert.ToBase64String(memoryStream.ToArray())
            };

            return Ok(response);
        }

        [HttpPost("extract-file")]
        public async Task<IActionResult> ExtractFromFile(
            [FromForm] ExtractFromFileRequest request,
            CancellationToken ct)
        {
            if (!LlamaRequestValidation.IsValid(request, out var errorMessage))
                return BadRequest(ResponseHandler.Error(errorMessage));

            var text = await _documentProcessor.ProcessAsync(request).ConfigureAwait(false);

            if (string.IsNullOrEmpty(text))
                return StatusCode(500, ResponseHandler.Error("No se pudo extraer contenido"));

            var metadata = new ChunkMetadata
            {
                DocumentName = request.File?.FileName ?? "uploaded",
                DocumentType = request.File?.ContentType ?? "unknown",
                Source = "user-upload"
            };
            var docChunks = _chunker.Chunk(text, metadata);

            _logger.LogInformation(
                "Document chunked into {Count} parts for '{File}'.",
                docChunks.Count,
                metadata.DocumentName);

            var userPrompt = request.Prompt;
            var relevantDocChunks = await RankChunksByRelevance(docChunks, userPrompt, ct);
            var legalChunks = await RetrieveLegalContext(userPrompt, ct);

            request.Prompt = ContextBuilder.Build(userPrompt, relevantDocChunks, legalChunks);

            var tokenCount = GptEncoding.GetEncoding("cl100k_base").CountTokens(request.Prompt);
            var numCtx = Math.Min(tokenCount + _responseBuffer, _maxNumCtx);

            request.Stream = false;
            request.Options = new RequestOptions
            {
                Temperature = 0,
                NumCtx = numCtx
            };

            _logger.LogInformation(
                "Sending {Tokens} prompt tokens to LLM with num_ctx {NumCtx} ({DocChunks} doc chunks + {LegalChunks} legal chunks).",
                tokenCount,
                numCtx,
                relevantDocChunks.Count,
                legalChunks.Count);

            var result = await _llamaService.ExtractInfoAsync(request).ConfigureAwait(false);

            if (!ShouldReviewPlainTextResponse(request, result))
                return StatusCode((int)result.StatusCode, result);

            var rawAnswer = result.Data as string ?? string.Empty;
            var reviewResult = await _answerReviewService.ReviewAnswerAsync(
                userPrompt,
                rawAnswer,
                "document_extract_text_response",
                forceSpanish: true,
                additionalContext:
                    "Validate the answer against the uploaded document and retrieved legal context. " +
                    "Preserve the requested output format. If the user asked for a document-style response, return clean Markdown only.",
                externalProvider: request.ExternalProvider,
                externalModel: request.ExternalModel,
                cancellationToken: ct);

            return StatusCode(
                (int)result.StatusCode,
                ResponseHandler.Success(reviewResult.FinalAnswer, statusCode: result.StatusCode));
        }

        private async Task<IReadOnlyList<DocumentChunk>> RankChunksByRelevance(
            IReadOnlyList<DocumentChunk> chunks,
            string query,
            CancellationToken ct)
        {
            if (chunks.Count <= MaxDocChunks)
                return chunks;

            var preFiltered = KeywordPreFilter(chunks, query, MaxDocChunks * 3);

            _logger.LogInformation(
                "Ranking {Total} chunks - pre-filtered to {PreFiltered}, selecting top {K} by relevance.",
                chunks.Count,
                preFiltered.Count,
                MaxDocChunks);

            var queryEmbedding = await _embedder.GenerateEmbeddingAsync(query, ct);
            var chunkTexts = preFiltered.Select(c => c.Text).ToList();
            var chunkEmbeddings = await _embedder.GenerateEmbeddingsAsync(chunkTexts, ct);

            return preFiltered
                .Select((chunk, i) => (
                    Chunk: chunk,
                    Score: VectorMath.CosineSimilarity(queryEmbedding, chunkEmbeddings[i])))
                .OrderByDescending(x => x.Score)
                .Take(MaxDocChunks)
                .Select(x => x.Chunk)
                .ToList();
        }

        private async Task<IReadOnlyList<DocumentChunk>> RetrieveLegalContext(
            string query,
            CancellationToken ct)
        {
            try
            {
                var queryEmbedding = await _embedder.GenerateEmbeddingAsync(query, ct);
                return await _vectorStore.SearchAsync(LegalCollection, queryEmbedding, LegalTopK, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No legal context available - vector store may be empty.");
                return [];
            }
        }

        private static IReadOnlyList<DocumentChunk> KeywordPreFilter(
            IReadOnlyList<DocumentChunk> chunks,
            string query,
            int limit)
        {
            var tokens = query
                .ToLowerInvariant()
                .Split([' ', ',', '.', ';', ':', '?', '!', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Length > 3)
                .ToHashSet();

            if (tokens.Count == 0 || chunks.Count <= limit)
                return chunks;

            return chunks
                .Select(chunk => new
                {
                    Chunk = chunk,
                    Score = tokens.Count(token => chunk.Text.Contains(token, StringComparison.OrdinalIgnoreCase))
                })
                .OrderByDescending(item => item.Score)
                .Take(limit)
                .Select(item => item.Chunk)
                .ToList();
        }

        private static bool ShouldReviewPlainTextResponse(ExtractFromFileRequest request, IResponse result)
        {
            if (!result.Success || result.Data is not string rawText || string.IsNullOrWhiteSpace(rawText))
                return false;

            return request.Format is null || string.IsNullOrWhiteSpace(request.Format.ToString());
        }
    }
}
