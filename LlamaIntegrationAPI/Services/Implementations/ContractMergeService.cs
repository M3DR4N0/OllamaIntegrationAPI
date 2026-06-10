using System.Text;
using LlamaIntegrationAPI.Models.Ai;
using LlamaIntegrationAPI.Models.Contracts;
using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Services.Ai;
using LlamaIntegrationAPI.Services.Interfaces;

namespace LlamaIntegrationAPI.Services.Implementations;

public class ContractMergeService(
    IDocumentParserService parser,
    IChunkingService chunker,
    ILLMService llm,
    IAiGatewayService aiGatewayService,
    IConfiguration configuration,
    ILogger<ContractMergeService> logger) : IContractMergeService
{
    private const int MaxBaseDocumentChars = 24000;
    private const int MaxSourceDocumentChars = 12000;
    private const int MaxBaseChunks = 16;
    private const int MaxSourceChunks = 8;
    private readonly int _providerTimeoutSeconds =
        int.TryParse(configuration["ContractMerge:ProviderTimeoutSeconds"], out var timeoutSeconds) && timeoutSeconds > 0
            ? timeoutSeconds
            : 60;

    private const string LocalSystemPrompt = """
        Eres un abogado experto en redaccion de contratos y revision legal.

        Recibiras un DOCUMENTO BASE y uno o mas DOCUMENTOS FUENTE.
        Tu tarea es integrar de forma organica las clausulas pertinentes dentro del documento base.

        REGLAS:
        - Usa unicamente la informacion contenida en los documentos y en la instruccion del usuario.
        - No inventes clausulas, partes, montos, fechas, obligaciones o definiciones que no esten respaldadas por el material proporcionado.
        - Conserva la coherencia juridica y la continuidad del borrador base.
        - Si detectas conflictos entre clausulas, armonizalos y deja notas breves cuando exista una ambiguedad material.
        - Prioriza que el resultado sea un borrador contractual consolidado y util para revision legal.
        - Responde siempre en espanol.
        """;

    private const string GeminiSystemInstruction = """
        Actua como abogado experto en redaccion contractual.
        Integra las clausulas de los documentos fuente dentro del documento base de forma organica y coherente.
        Mantente estrictamente dentro del contenido provisto.
        Si existen conflictos materiales entre clausulas, armonizalos y senalalos brevemente.
        Devuelve un borrador contractual consolidado, claro y profesional, en espanol.
        """;

    public async Task<ContractMergeResult> MergeContractsAsync(
        ContractMergeRequest request,
        CancellationToken ct = default)
    {
        if (request.Files.Count < 2)
            throw new InvalidOperationException("Se requieren al menos dos archivos para fusionar contratos.");

        if (request.BaseDocumentIndex < 0 || request.BaseDocumentIndex >= request.Files.Count)
            throw new InvalidOperationException("BaseDocumentIndex esta fuera del rango de archivos recibidos.");

#pragma warning disable CS0618
        var effectiveQuery = string.IsNullOrWhiteSpace(request.Query)
            ? (string.IsNullOrWhiteSpace(request.Prompt)
                ? ContractMergeRequest.DefaultQuery
                : request.Prompt.Trim())
            : request.Query.Trim();
#pragma warning restore CS0618

        var preparedDocuments = new List<PreparedDocument>(request.Files.Count);

        for (var i = 0; i < request.Files.Count; i++)
        {
            var file = request.Files[i];
            var text = await parser.ExtractTextAsync(file);

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException(
                    $"No se pudo extraer texto del archivo '{file.FileName}'.");
            }

            var isBaseDocument = i == request.BaseDocumentIndex;
            var excerpt = BuildDocumentExcerpt(
                text,
                file.FileName,
                file.ContentType,
                effectiveQuery,
                isBaseDocument);

            preparedDocuments.Add(new PreparedDocument(
                i,
                file.FileName,
                file.ContentType,
                text.Length,
                isBaseDocument,
                excerpt));
        }

        var baseDocument = preparedDocuments.Single(d => d.IsBaseDocument);
        var sourceDocuments = preparedDocuments.Where(d => !d.IsBaseDocument).ToList();

        var localUserPrompt = BuildLocalUserPrompt(effectiveQuery, baseDocument, sourceDocuments);
        var geminiContext = BuildGeminiContext(baseDocument, sourceDocuments);

        logger.LogInformation(
            "Starting sequential contract merge generation: local model first, Gemini second.");

        var ollamaResult = await GenerateLocalDraftAsync(localUserPrompt, request.Model, ct);
        var geminiResult = await GenerateGeminiDraftAsync(effectiveQuery, geminiContext, request.ForceSpanish, ct);

        if (!ollamaResult.Success && !geminiResult.Success)
        {
            throw new InvalidOperationException(
                "No se pudo generar ninguna propuesta de fusion. " +
                $"Ollama: {ollamaResult.Error ?? "sin detalle"}. " +
                $"Gemini: {geminiResult.Error ?? "sin detalle"}.");
        }

        var primaryAnswer = !string.IsNullOrWhiteSpace(geminiResult.Text)
            ? geminiResult.Text
            : ollamaResult.Text ?? string.Empty;

        return new ContractMergeResult
        {
            Answer = primaryAnswer,
            OllamaAnswer = ollamaResult.Text,
            GeminiAnswer = geminiResult.Text,
            OllamaError = ollamaResult.Error,
            GeminiError = geminiResult.Error,
            DocumentsProcessed = preparedDocuments.Count,
            BaseDocumentName = baseDocument.FileName
        };
    }

    private async Task<ProviderExecutionResult> GenerateLocalDraftAsync(
        string userPrompt,
        string model,
        CancellationToken ct)
    {
        try
        {
            var text = await llm.GenerateAsync(LocalSystemPrompt, userPrompt, model, ct);
            logger.LogInformation(
                "Local contract merge generation completed using model '{Model}'.",
                model);
            return ProviderExecutionResult.FromSuccess(text);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local contract merge generation failed.");
            return ProviderExecutionResult.FromError(ex.Message);
        }
    }

    private async Task<ProviderExecutionResult> GenerateGeminiDraftAsync(
        string prompt,
        string context,
        bool forceSpanish,
        CancellationToken ct)
    {
        try
        {
            using var timeoutScope = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutScope.CancelAfter(TimeSpan.FromSeconds(_providerTimeoutSeconds));

            var response = await aiGatewayService.GenerateAsync(
                new AiGenerateRequest
                {
                    Task = "contract_merge",
                    Prompt = prompt,
                    Context = context,
                    ForceSpanish = forceSpanish,
                    Metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["provider"] = "Gemini"
                    },
                    SystemInstruction = GeminiSystemInstruction
                },
                timeoutScope.Token);

            if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
            {
                return ProviderExecutionResult.FromError(
                    response.Error ?? "Gemini no devolvio una respuesta util.");
            }

            logger.LogInformation("Gemini contract merge generation completed.");
            return ProviderExecutionResult.FromSuccess(response.Text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Gemini contract merge generation exceeded the timeout of {TimeoutSeconds} seconds.",
                _providerTimeoutSeconds);
            return ProviderExecutionResult.FromError(
                $"Gemini excedio el tiempo limite de {_providerTimeoutSeconds} segundos.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gemini contract merge generation failed.");
            return ProviderExecutionResult.FromError(ex.Message);
        }
    }

    private string BuildLocalUserPrompt(
        string prompt,
        PreparedDocument baseDocument,
        IReadOnlyList<PreparedDocument> sourceDocuments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== INSTRUCCION DEL USUARIO ===");
        sb.AppendLine(prompt);
        sb.AppendLine();
        sb.AppendLine("=== DOCUMENTO BASE (BORRADOR EXISTENTE) ===");
        sb.AppendLine($"[Archivo: {baseDocument.FileName}]");
        sb.AppendLine(baseDocument.Excerpt);
        sb.AppendLine();
        sb.AppendLine("=== DOCUMENTOS FUENTE PARA INTEGRAR ===");

        foreach (var document in sourceDocuments)
        {
            sb.AppendLine($"[Archivo: {document.FileName}]");
            sb.AppendLine(document.Excerpt);
            sb.AppendLine();
        }

        sb.AppendLine("=== INSTRUCCION OBLIGATORIA ===");
        sb.AppendLine(
            "Devuelve un borrador contractual consolidado. " +
            "Integra las clausulas aplicables dentro del documento base y, si detectas conflictos, " +
            "senalalos brevemente al final bajo un apartado 'Notas de revision'.");

        return sb.ToString().Trim();
    }

    private string BuildGeminiContext(
        PreparedDocument baseDocument,
        IReadOnlyList<PreparedDocument> sourceDocuments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DOCUMENTO BASE (BORRADOR EXISTENTE)");
        sb.AppendLine($"Archivo: {baseDocument.FileName}");
        sb.AppendLine(baseDocument.Excerpt);
        sb.AppendLine();
        sb.AppendLine("DOCUMENTOS FUENTE PARA INTEGRAR");

        foreach (var document in sourceDocuments)
        {
            sb.AppendLine($"Archivo: {document.FileName}");
            sb.AppendLine(document.Excerpt);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private string BuildDocumentExcerpt(
        string text,
        string fileName,
        string contentType,
        string prompt,
        bool isBaseDocument)
    {
        var normalizedText = text.Trim();
        var maxChars = isBaseDocument ? MaxBaseDocumentChars : MaxSourceDocumentChars;

        if (normalizedText.Length <= maxChars)
            return normalizedText;

        var chunks = chunker.Chunk(
            normalizedText,
            new ChunkMetadata
            {
                DocumentName = fileName,
                DocumentType = contentType,
                Source = isBaseDocument ? "contract-merge-base" : "contract-merge-source"
            });

        var selectedChunks = SelectRelevantChunks(
            chunks,
            prompt,
            isBaseDocument ? MaxBaseChunks : MaxSourceChunks,
            includeLeadingChunks: isBaseDocument ? 2 : 1);

        var sb = new StringBuilder();
        sb.AppendLine("[Documento recortado para ajustarse al contexto. Se muestran fragmentos relevantes.]");
        sb.AppendLine();

        foreach (var chunk in selectedChunks)
        {
            var label = chunk.Metadata.Article ?? chunk.Metadata.Section;
            if (!string.IsNullOrWhiteSpace(label))
                sb.AppendLine($"[{label}]");

            sb.AppendLine(chunk.Text);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static IReadOnlyList<DocumentChunk> SelectRelevantChunks(
        IReadOnlyList<DocumentChunk> chunks,
        string prompt,
        int limit,
        int includeLeadingChunks)
    {
        if (chunks.Count <= limit)
            return chunks;

        var indexedChunks = chunks
            .Select((chunk, index) => new IndexedChunk(chunk, index))
            .ToList();

        var leading = indexedChunks.Take(includeLeadingChunks);
        var promptTokens = Tokenize(prompt);

        var scored = indexedChunks
            .Select(indexed => new ScoredChunk(
                indexed.Chunk,
                indexed.Index,
                promptTokens.Count == 0
                    ? 0
                    : promptTokens.Count(token =>
                        indexed.Chunk.Text.Contains(token, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index);

        var selected = leading
            .Select(item => item.Index)
            .Concat(scored.Select(item => item.Index))
            .Distinct()
            .Take(limit)
            .OrderBy(index => index)
            .Select(index => chunks[index])
            .ToList();

        return selected;
    }

    private static HashSet<string> Tokenize(string text)
    {
        return text
            .ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '?', '!', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 3)
            .ToHashSet();
    }

    private sealed record PreparedDocument(
        int Index,
        string FileName,
        string ContentType,
        int CharacterCount,
        bool IsBaseDocument,
        string Excerpt);

    private sealed record IndexedChunk(DocumentChunk Chunk, int Index);

    private sealed record ScoredChunk(DocumentChunk Chunk, int Index, int Score);

    private sealed record ProviderExecutionResult(bool Success, string? Text, string? Error)
    {
        public static ProviderExecutionResult FromSuccess(string text) => new(true, text, null);
        public static ProviderExecutionResult FromError(string error) => new(false, null, error);
    }
}
