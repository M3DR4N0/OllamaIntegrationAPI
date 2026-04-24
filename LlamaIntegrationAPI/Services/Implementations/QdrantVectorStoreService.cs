using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Services.Interfaces;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace LlamaIntegrationAPI.Services.Implementations;

/// <summary>
/// Qdrant-backed vector store for legal document chunks.
/// Supports collection management, upsert, and similarity search.
/// </summary>
public class QdrantVectorStoreService : IVectorStoreService
{
    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorStoreService> _logger;

    // Payload field names — single source of truth
    private const string FieldText = "text";
    private const string FieldDocName = "document_name";
    private const string FieldDocType = "document_type";
    private const string FieldSection = "section";
    private const string FieldArticle = "article";
    private const string FieldSource = "source";

    public QdrantVectorStoreService(IConfiguration config, ILogger<QdrantVectorStoreService> logger)
    {
        var host = config["QDRANT_HOST"] ?? "localhost";
        var port = int.TryParse(config["QDRANT_PORT"], out var p) ? p : 6334;

        _client = new QdrantClient(host, port);
        _logger = logger;
    }

    public async Task EnsureCollectionAsync(string collectionName, int vectorSize, CancellationToken ct = default)
    {
        if (await _client.CollectionExistsAsync(collectionName, ct))
        {
            _logger.LogDebug("Collection '{Collection}' already exists.", collectionName);
            return;
        }

        try
        {
            await _client.CreateCollectionAsync(
                collectionName,
                new VectorParams
                {
                    Size = (ulong)vectorSize,
                    Distance = Distance.Cosine
                },
                cancellationToken: ct);

            _logger.LogInformation("Created Qdrant collection '{Collection}' (dims={Dims}).", collectionName, vectorSize);
        }
        catch (Exception)
        {
            // Another request may have created the collection between our check and create.
            if (await _client.CollectionExistsAsync(collectionName, ct))
            {
                _logger.LogDebug("Collection '{Collection}' was created by a concurrent request.", collectionName);
            }
            else
            {
                throw;
            }
        }
    }

    public async Task UpsertAsync(string collectionName, IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        var points = chunks.Select(ToPointStruct).ToList();

        if (points.Count == 0)
            return;

        // Qdrant accepts batches up to ~1000 points efficiently
        const int batchSize = 256;

        for (int i = 0; i < points.Count; i += batchSize)
        {
            var batch = points.Skip(i).Take(batchSize).ToList();
            await _client.UpsertAsync(collectionName, batch, cancellationToken: ct);
            _logger.LogDebug("Upserted batch {Start}-{End} of {Total} points.", i, i + batch.Count, points.Count);
        }

        _logger.LogInformation("Upserted {Count} points into '{Collection}'.", points.Count, collectionName);
    }

    public async Task<IReadOnlyList<DocumentChunk>> SearchAsync(
        string collectionName, float[] queryVector, int topK = 5, CancellationToken ct = default)
    {
        var results = await _client.SearchAsync(
            collectionName,
            queryVector,
            limit: (ulong)topK,
            cancellationToken: ct);

        var chunks = results.Select(ToDocumentChunk).ToList();

        _logger.LogInformation("Search returned {Count} results from '{Collection}'.", chunks.Count, collectionName);
        return chunks;
    }

    // ── Mapping helpers ──────────────────────────────────────────────

    private static PointStruct ToPointStruct(DocumentChunk chunk)
    {
        if (chunk.Embedding is null || chunk.Embedding.Length == 0)
            throw new ArgumentException($"Chunk '{chunk.Id}' has no embedding — cannot upsert to Qdrant.");

        var point = new PointStruct
        {
            Id = new PointId { Uuid = chunk.Id.ToString() },
            Vectors = chunk.Embedding
        };

        point.Payload[FieldText] = chunk.Text;
        point.Payload[FieldDocName] = chunk.Metadata.DocumentName;
        point.Payload[FieldDocType] = chunk.Metadata.DocumentType;
        point.Payload[FieldSource] = chunk.Metadata.Source;

        if (chunk.Metadata.Section is not null)
            point.Payload[FieldSection] = chunk.Metadata.Section;

        if (chunk.Metadata.Article is not null)
            point.Payload[FieldArticle] = chunk.Metadata.Article;

        return point;
    }

    private static DocumentChunk ToDocumentChunk(ScoredPoint scored)
    {
        return new DocumentChunk
        {
            Id = Guid.TryParse(scored.Id.Uuid, out var id) ? id : Guid.NewGuid(),
            Text = GetPayloadString(scored, FieldText),
            Metadata = new ChunkMetadata
            {
                DocumentName = GetPayloadString(scored, FieldDocName),
                DocumentType = GetPayloadString(scored, FieldDocType),
                Section = GetPayloadStringOrNull(scored, FieldSection),
                Article = GetPayloadStringOrNull(scored, FieldArticle),
                Source = GetPayloadString(scored, FieldSource)
            }
        };
    }

    private static string GetPayloadString(ScoredPoint point, string key) =>
        point.Payload.TryGetValue(key, out var val) ? val.StringValue : string.Empty;

    private static string? GetPayloadStringOrNull(ScoredPoint point, string key) =>
        point.Payload.TryGetValue(key, out var val) ? val.StringValue : null;
}
