using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models.Ai;
using LlamaIntegrationAPI.Models.Contracts;
using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Services.Ai;
using LlamaIntegrationAPI.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace LlamaIntegrationAPI.Services.Implementations;

public class ContractMergeService(
    IDocumentParserService parser,
    IChunkingService chunker,
    ILLMService llm,
    IAiGatewayService aiGatewayService,
    IOptionsMonitor<AiOptions> aiOptionsMonitor,
    IConfiguration configuration,
    ILogger<ContractMergeService> logger) : IContractMergeService
{
    private const string CompletionMarker = "FIN DEL CONTRATO";
    private static readonly Regex SourceClausePlaceholderPattern = new(
        @"secuencia[_\s-]*clausulas|^«.*»$|^<<.*>>$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ParagraphOrdinalPattern = new(
        @"P[ÁA]RRAFO\s+([IVXLCDM]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ContractIndicatorPattern = new(
        @"\bcontrato\b|en fe de lo cual|las partes|objeto del contrato|vigencia|terminacion|firmas|por el cliente|por el proveedor",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClauseFileNamePattern = new(
        @"claus|adenda|anexo|anexos|terminos",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClauseSegmentStartPattern = new(
        @"(?i)(?=(?:^|[\r\n]+|\s{2,})(?:P[ÁA]RRAFO|NUMERAL|INCISO|LITERAL|ART[ÍI]CULO|CL[ÁA]USULA)\s+(?:[IVXLCDM]+|\d+|[A-Z]))",
        RegexOptions.Compiled);

    private readonly int _localBaseDocumentChars = ResolvePositiveInt(
        configuration["ContractMerge:LocalBaseDocumentChars"], 16000);
    private readonly int _localSourceDocumentChars = ResolvePositiveInt(
        configuration["ContractMerge:LocalSourceDocumentChars"], 7000);
    private readonly int _localBaseChunks = ResolvePositiveInt(
        configuration["ContractMerge:LocalBaseChunks"], 10);
    private readonly int _localSourceChunks = ResolvePositiveInt(
        configuration["ContractMerge:LocalSourceChunks"], 4);
    private readonly int _reviewBaseDocumentChars = ResolvePositiveInt(
        configuration["ContractMerge:ReviewBaseDocumentChars"], 22000);
    private readonly int _reviewSourceDocumentChars = ResolvePositiveInt(
        configuration["ContractMerge:ReviewSourceDocumentChars"], 10000);
    private readonly int _reviewBaseChunks = ResolvePositiveInt(
        configuration["ContractMerge:ReviewBaseChunks"], 14);
    private readonly int _reviewSourceChunks = ResolvePositiveInt(
        configuration["ContractMerge:ReviewSourceChunks"], 6);
    private readonly int _localMaxPredict = ResolvePositiveInt(
        configuration["ContractMerge:LocalMaxPredict"], 4096);
    private readonly int _continuationMaxPredict = ResolvePositiveInt(
        configuration["ContractMerge:ContinuationMaxPredict"], 2200);
    private readonly int _maxContinuationPasses = ResolvePositiveInt(
        configuration["ContractMerge:MaxContinuationPasses"], 3);
    private readonly int _providerTimeoutSeconds =
        int.TryParse(configuration["ContractMerge:ProviderTimeoutSeconds"], out var timeoutSeconds) && timeoutSeconds > 0
            ? timeoutSeconds
            : 10800;

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
        - Devuelve solo Markdown valido y limpio, sin bloques de codigo y sin texto fuera del documento.
        - Usa encabezados, listas y numeracion solo cuando ayuden a una futura conversion a Microsoft Word.
        - Si el documento consolidado funciona mejor como clausulas numeradas en texto corrido, prioriza ese formato.
        - No resumas el contrato.
        - El documento debe quedar completo.
        - Termina SIEMPRE con una linea final exacta: FIN DEL CONTRATO
        - No escribas nada despues de esa linea final.
        - Responde siempre en espanol.
        """;

    private const string ContinuationSystemPrompt = """
        Eres un abogado experto en redaccion contractual.

        Recibiras un borrador contractual en Markdown que quedo incompleto.
        Tu tarea es continuarlo exactamente desde donde se corto.

        REGLAS:
        - No repitas encabezados ni texto ya escrito.
        - No reinicies el contrato desde el principio.
        - Continualo desde la ultima frase o clausula incompleta.
        - Manten el mismo formato Markdown y la misma estructura.
        - No resumas.
        - Debes completar el contrato.
        - Termina SIEMPRE con una linea final exacta: FIN DEL CONTRATO
        - No escribas nada despues de esa linea final.
        - Responde siempre en espanol.
        """;

    private const string ExternalReviewSystemInstruction = """
        Actua como revisor legal y editor de formato Markdown para documentos contractuales.
        Recibiras la instruccion original del usuario, el contexto documental y un borrador completo generado por el modelo local.

        Tu tarea no es rehacer el documento desde cero.
        Primero valida si el contenido tiene sentido frente al prompt y a los documentos.
        Luego corrige solamente lo necesario para:
        - mantener coherencia juridica,
        - evitar invenciones,
        - mejorar claridad,
        - corregir pequenos errores,
        - y asegurar que la salida sea Markdown valido y limpio para conversion a Microsoft Word.

        REGLAS DE SALIDA:
        - Devuelve el documento COMPLETO.
        - No resumas ni acortes el borrador local.
        - Conserva todas las clausulas utiles presentes en el borrador local, salvo correcciones justificadas.
        - No uses bloques de codigo.
        - No agregues comentarios meta.
        - Si falta informacion esencial, senalalo solo dentro de una seccion final 'Notas de revision'.
        - Termina SIEMPRE con una linea final exacta: FIN DEL CONTRATO
        - No escribas nada despues de esa linea final.
        - Responde siempre en espanol.
        """;

    private const string DocxPlanSystemInstruction = """
        Actua como abogado redactor y editor estructural de contratos en formato Word.

        Recibiras:
        - la instruccion del usuario,
        - un mapa estructural del documento base en formato DOCX,
        - y uno o mas documentos fuente con clausulas para insertar.

        Tu tarea es decidir que clausulas del documento fuente deben agregarse al documento base y en que punto logico insertarlas,
        preservando al maximo la estructura y estilo del documento base.

        Debes responder SOLO JSON valido con este esquema:
        {
          "summary": "descripcion breve",
          "operations": [
            {
              "targetBlockId": "block_0004",
              "placement": "before",
              "sourceClauseId": "clause_001",
              "heading": "titulo opcional de la nueva clausula",
              "content": "texto opcional en varios parrafos",
              "paragraphs": ["parrafo 1", "parrafo 2"],
              "reason": "motivo breve"
            }
          ]
        }

        Ejemplos validos de targetBlockId:
        - "block_0004"
        - "__before_signatures__"
        - "__document_end__"

        Ejemplos validos de placement:
        - "before"
        - "after"
        - "before_signatures"
        - "append_end"

        REGLAS:
        - No reescribas todo el contrato.
        - Solo crea operaciones para clausulas nuevas o necesarias.
        - Usa exclusivamente informacion sustentada por el catalogo de clausulas detectadas.
        - Un mismo documento fuente puede contener varias clausulas independientes sobre temas distintos. Tratalas por separado y crea operaciones distintas cuando corresponda.
        - Si una clausula ya existe en el documento base, no la dupliques.
        - No repitas la misma clausula en multiples operaciones ni en multiples bloques.
        - No devuelvas fragmentos sueltos, textos aleatorios, ni lineas incompletas. Cada operacion debe representar una insercion juridica coherente y util.
        - Usa solo targetBlockId existentes en el mapa del documento base o las anclas especiales permitidas.
        - Cada operacion debe referenciar una clausula del catalogo usando sourceClauseId.
        - No inventes nuevas clausulas ni redefines el texto juridico. Debes reutilizar el texto de la clausula fuente con cambios minimos de formato solamente cuando sean necesarios para insertarlo.
        - Antes de elegir targetBlockId, compara el tema juridico central de la clausula fuente con el encabezado y extracto de cada bloque del contrato base. Inserta la clausula en el bloque con mayor afinidad tematica.
        - Prioriza decidir: que clausula insertar y en que bloque insertarla. El sistema se encargara de ordenar encabezado y cuerpo al integrarlo.
        - Si vas a insertar un nuevo PARRAFO, NUMERAL, INCISO o LITERAL dentro de un ARTICULO o CLAUSULA ya existente, usa el targetBlockId de ese articulo o clausula y placement "before". El sistema lo insertara inmediatamente despues del encabezado del bloque y antes del contenido existente.
        - En esos casos, coloca el rotulo corto en "heading" (por ejemplo: "PARRAFO I: Cobertura...") y deja el texto explicativo dentro de "paragraphs" o "content".
        - No coloques el contenido antes del "heading" ni repitas el "heading" dentro de "paragraphs".
        - Si el lugar mas adecuado es antes de firmas, usa targetBlockId "__before_signatures__".
        - Si debe ir al final y no hay mejor ancla tematica razonable, usa "__document_end__" con placement "append_end".
        - Responde siempre en espanol.
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
            var localExcerpt = BuildDocumentExcerpt(
                text,
                file.FileName,
                file.ContentType,
                effectiveQuery,
                ResolveExcerptBudget(isBaseDocument, forReview: false));
            var reviewExcerpt = BuildDocumentExcerpt(
                text,
                file.FileName,
                file.ContentType,
                effectiveQuery,
                ResolveExcerptBudget(isBaseDocument, forReview: true));

            preparedDocuments.Add(new PreparedDocument(
                i,
                file.FileName,
                file.ContentType,
                text.Length,
                isBaseDocument,
                localExcerpt,
                reviewExcerpt));
        }

        var baseDocument = preparedDocuments.Single(d => d.IsBaseDocument);
        var sourceDocuments = preparedDocuments.Where(d => !d.IsBaseDocument).ToList();

        var localUserPrompt = BuildLocalUserPrompt(effectiveQuery, baseDocument, sourceDocuments);
        var reviewContext = BuildReviewContext(baseDocument, sourceDocuments);

        logger.LogInformation(
            "Prepared contract merge context with {DocumentsProcessed} documents. Local prompt chars: {LocalPromptChars}. Review context chars: {ReviewContextChars}.",
            preparedDocuments.Count,
            localUserPrompt.Length,
            reviewContext.Length);

        logger.LogInformation(
            "Starting sequential contract merge generation: local model first, external review second.");

        var ollamaResult = await GenerateCompleteLocalDraftAsync(localUserPrompt, request.Model, ct);

        if (!ollamaResult.Success || string.IsNullOrWhiteSpace(ollamaResult.Text))
        {
            throw new InvalidOperationException(
                "No se pudo completar el borrador contractual con el modelo local. " +
                $"Detalle: {ollamaResult.Error ?? "sin detalle"}.");
        }

        var reviewedResult = await ReviewMarkdownDraftAsync(
            effectiveQuery,
            reviewContext,
            ollamaResult.Text,
            request.ForceSpanish,
            request.ExternalProvider,
            request.ExternalModel,
            ct);

        var primaryAnswer = SelectPrimaryAnswer(ollamaResult.Text, reviewedResult);
        var finalMarkdown = RemoveCompletionMarker(primaryAnswer);
        var wordDocument = MarkdownWordConverter.ConvertToDocx(
            finalMarkdown,
            title: Path.GetFileNameWithoutExtension(baseDocument.FileName));

        return new ContractMergeResult
        {
            Answer = finalMarkdown,
            AnswerFormat = "markdown",
            OllamaAnswer = RemoveCompletionMarker(ollamaResult.Text),
            GeminiAnswer = string.IsNullOrWhiteSpace(reviewedResult.Text)
                ? reviewedResult.Text
                : RemoveCompletionMarker(reviewedResult.Text),
            OllamaError = ollamaResult.Error,
            GeminiError = reviewedResult.Error,
            DocumentsProcessed = preparedDocuments.Count,
            BaseDocumentName = baseDocument.FileName,
            WordDocument = wordDocument,
            WordDocumentFileName = BuildMergedFileName(baseDocument.FileName),
            WordDocumentContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        };
    }

    public async Task<ContractMergeResult> MergeContractsPreservingOriginalDocxAsync(
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

        var mergeCandidates = await AnalyzeMergeDocumentCandidatesAsync(request, ct);
        var baseCandidate = ResolveBaseDocumentCandidate(request, mergeCandidates);

        if (baseCandidate.DocxBytes is null)
        {
            throw new InvalidOperationException(
                "La preservacion de formato requiere que el documento base identificado sea un archivo .docx.");
        }

        var sourceCandidates = mergeCandidates
            .Where(candidate => candidate.Index != baseCandidate.Index)
            .ToList();

        if (sourceCandidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No se pudo identificar un documento fuente distinto al contrato base.");
        }

        var baseFile = request.Files[baseCandidate.Index];
        var baseDocxBytes = baseCandidate.DocxBytes;
        var baseBlocks = baseCandidate.BaseBlocks;

        if (baseBlocks.Count == 0)
        {
            throw new InvalidOperationException(
                "No se pudo identificar una estructura util dentro del documento base para insertar clausulas.");
        }

        var sourceClauses = sourceCandidates
            .SelectMany(candidate => candidate.SourceClauses)
            .ToList();

        if (sourceClauses.Count == 0)
        {
            throw new InvalidOperationException(
                "No se detectaron clausulas utilizables en los documentos fuente. " +
                "Verifique que el documento de clausulas realmente contenga clausulas estructuradas y no texto libre.");
        }

        var planResult = await BuildDocxMergePlanAsync(
            effectiveQuery,
            request,
            baseBlocks,
            sourceClauses,
            ct);

        if (!planResult.Success || planResult.Plan is null)
        {
            throw new InvalidOperationException(
                "No se pudo construir el plan de insercion de clausulas para el documento Word. " +
                $"Detalle: {planResult.Error ?? "sin detalle"}.");
        }

        var operations = SanitizeDocxOperations(planResult.Plan.Operations, baseBlocks, sourceClauses)
            .Where(operation => !string.IsNullOrWhiteSpace(operation.Content) ||
                                operation.Paragraphs.Count > 0 ||
                                !string.IsNullOrWhiteSpace(operation.Heading))
            .ToList();

        if (operations.Count == 0)
        {
            throw new InvalidOperationException(
                "La IA no genero ninguna operacion de insercion para el documento Word. " +
                "No se devolvera el documento base sin cambios. Revise el prompt, reduzca el tamano del contexto o intente nuevamente.");
        }

        var mergedDocx = DocxOriginalFormatMerger.ApplyOperations(baseDocxBytes, operations);

        var summary = !string.IsNullOrWhiteSpace(planResult.Plan.Summary)
            ? planResult.Plan.Summary
            : BuildDocxOperationSummary(operations);

        return new ContractMergeResult
        {
            Answer = summary,
            AnswerFormat = "docx_operation_summary",
            OllamaAnswer = planResult.Provider == "local" ? planResult.RawText : null,
            GeminiAnswer = planResult.Provider == "external" ? planResult.RawText : null,
            GeminiError = planResult.Provider == "external" ? null : planResult.Error,
            DocumentsProcessed = request.Files.Count,
            BaseDocumentName = baseCandidate.FileName,
            WordDocument = mergedDocx,
            WordDocumentFileName = BuildMergedFileName(baseCandidate.FileName),
            WordDocumentContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        };
    }

    private async Task<List<MergeDocumentCandidate>> AnalyzeMergeDocumentCandidatesAsync(
        ContractMergeRequest request,
        CancellationToken ct)
    {
        var candidates = new List<MergeDocumentCandidate>(request.Files.Count);

        for (var i = 0; i < request.Files.Count; i++)
        {
            var file = request.Files[i];
            byte[]? docxBytes = null;
            string extractedText;
            IReadOnlyList<DocxBlockSummary> baseBlocks = [];

            if (DocxOriginalFormatMerger.LooksLikeDocx(file))
            {
                docxBytes = await ReadAllBytesAsync(file, ct);
                await using var sourceStream = new MemoryStream(docxBytes, writable: false);
                extractedText = await parser.ExtractTextAsync(sourceStream, file.ContentType);

                try
                {
                    baseBlocks = DocxOriginalFormatMerger.Summarize(docxBytes);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not summarize DOCX structure for file '{FileName}'.", file.FileName);
                }
            }
            else
            {
                extractedText = await parser.ExtractTextAsync(file);
            }

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                throw new InvalidOperationException(
                    $"No se pudo extraer texto del archivo '{file.FileName}'.");
            }

            var sourceClauses = ExtractSourceClauseCandidates(
                extractedText,
                file.FileName,
                file.ContentType,
                docxBytes);

            candidates.Add(new MergeDocumentCandidate(
                i,
                file.FileName,
                file.ContentType,
                extractedText,
                docxBytes,
                baseBlocks,
                sourceClauses,
                ComputeContractDocumentScore(file.FileName, extractedText, baseBlocks, docxBytes is not null),
                ComputeClauseDocumentScore(file.FileName, extractedText, sourceClauses)));
        }

        return candidates;
    }

    private MergeDocumentCandidate ResolveBaseDocumentCandidate(
        ContractMergeRequest request,
        IReadOnlyList<MergeDocumentCandidate> candidates)
    {
        var explicitCandidate = candidates
            .FirstOrDefault(candidate => candidate.Index == request.BaseDocumentIndex);

        var bestDetectedCandidate = candidates
            .Where(candidate => candidate.DocxBytes is not null)
            .OrderByDescending(candidate => candidate.ContractScore - (candidate.ClauseScore * 0.35))
            .ThenByDescending(candidate => candidate.BaseBlocks.Count)
            .ThenByDescending(candidate => candidate.ExtractedText.Length)
            .FirstOrDefault();

        if (bestDetectedCandidate is not null &&
            bestDetectedCandidate.ContractScore >= Math.Max(3d, bestDetectedCandidate.ClauseScore))
        {
            logger.LogInformation(
                "Auto-identified base contract document as '{FileName}' (contractScore: {ContractScore}, clauseScore: {ClauseScore}).",
                bestDetectedCandidate.FileName,
                bestDetectedCandidate.ContractScore,
                bestDetectedCandidate.ClauseScore);
            return bestDetectedCandidate;
        }

        if (explicitCandidate is not null && explicitCandidate.DocxBytes is not null)
        {
            logger.LogInformation(
                "Falling back to explicit BaseDocumentIndex for document '{FileName}'.",
                explicitCandidate.FileName);
            return explicitCandidate;
        }

        return bestDetectedCandidate
               ?? throw new InvalidOperationException(
                   "No se pudo identificar un documento base .docx para preservar el formato original.");
    }

    private static double ComputeContractDocumentScore(
        string fileName,
        string extractedText,
        IReadOnlyList<DocxBlockSummary> baseBlocks,
        bool isDocx)
    {
        var score = 0d;
        var normalizedFileName = NormalizeComparisonText(fileName);
        var normalizedText = NormalizeOptionalText(extractedText) ?? string.Empty;

        if (isDocx)
            score += 1.5;

        if (normalizedFileName.Contains("contrato", StringComparison.OrdinalIgnoreCase))
            score += 2.5;

        if (ContractIndicatorPattern.IsMatch(normalizedText))
            score += 4;

        if (normalizedText.Contains("en fe de lo cual", StringComparison.OrdinalIgnoreCase))
            score += 3;

        if (normalizedText.Contains("las partes", StringComparison.OrdinalIgnoreCase))
            score += 1.5;

        if (normalizedText.Contains("objeto", StringComparison.OrdinalIgnoreCase) &&
            normalizedText.Contains("contrato", StringComparison.OrdinalIgnoreCase))
        {
            score += 1.5;
        }

        if (baseBlocks.Count >= 4)
            score += 1;

        if (baseBlocks.Any(block => block.IsSignatureBlock))
            score += 2;

        return score;
    }

    private static double ComputeClauseDocumentScore(
        string fileName,
        string extractedText,
        IReadOnlyList<SourceClauseCandidate> sourceClauses)
    {
        var score = 0d;
        var normalizedFileName = NormalizeComparisonText(fileName);
        var normalizedText = NormalizeOptionalText(extractedText) ?? string.Empty;

        if (ClauseFileNamePattern.IsMatch(normalizedFileName))
            score += 3;

        if (SourceClausePlaceholderPattern.IsMatch(normalizedText))
            score += 3;

        score += Math.Min(6, sourceClauses.Count) * 0.75;

        if (sourceClauses.Count >= 2)
            score += 1.5;

        return score;
    }

    private async Task<ProviderExecutionResult> GenerateCompleteLocalDraftAsync(
        string userPrompt,
        string model,
        CancellationToken ct)
    {
        try
        {
            var draft = NormalizeMarkdownOutput(await llm.GenerateAsync(
                LocalSystemPrompt,
                userPrompt,
                model,
                ct,
                maxPredict: _localMaxPredict));

            logger.LogInformation(
                "Local contract merge generation completed using model '{Model}' with maxPredict {MaxPredict}.",
                model,
                _localMaxPredict);

            for (var pass = 1; pass <= _maxContinuationPasses && !HasCompletionMarker(draft); pass++)
            {
                logger.LogWarning(
                    "Local contract merge draft appears incomplete. Running continuation pass {Pass}/{MaxPasses}.",
                    pass,
                    _maxContinuationPasses);

                var continuation = NormalizeMarkdownOutput(await llm.GenerateAsync(
                    ContinuationSystemPrompt,
                    BuildContinuationPrompt(userPrompt, draft),
                    model,
                    ct,
                    maxPredict: _continuationMaxPredict));

                if (string.IsNullOrWhiteSpace(continuation))
                    break;

                draft = AppendContinuation(draft, continuation);
            }

            if (!HasCompletionMarker(draft))
            {
                logger.LogWarning("Local contract merge draft is still incomplete after all continuation passes.");
                return ProviderExecutionResult.FromError(
                    "El modelo local devolvio un borrador incompleto incluso despues de los reintentos de continuacion.");
            }

            return ProviderExecutionResult.FromSuccess(draft);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local contract merge generation failed.");
            return ProviderExecutionResult.FromError(ex.Message);
        }
    }

    private async Task<DocxPlanGenerationResult> BuildDocxMergePlanAsync(
        string prompt,
        ContractMergeRequest request,
        IReadOnlyList<DocxBlockSummary> baseBlocks,
        IReadOnlyList<SourceClauseCandidate> sourceClauses,
        CancellationToken ct)
    {
        var sourceContext = BuildSourceClauseCatalogContext(sourceClauses);
        var planContext = BuildDocxPlanContext(prompt, baseBlocks, sourceContext);

        if (!aiOptionsMonitor.CurrentValue.UseExternalProviders)
        {
            logger.LogInformation("External providers are disabled. Building DOCX merge plan with the local model only.");
            return await TryBuildDocxPlanWithLocalModelAsync(
                prompt,
                planContext,
                request.Model,
                ct);
        }

        var externalResult = await TryBuildDocxPlanWithExternalAiAsync(
            prompt,
            planContext,
            request,
            ct);

        if (externalResult.Success)
            return externalResult;

        logger.LogWarning(
            "External DOCX merge plan generation failed. Falling back to local model. Error: {Error}",
            externalResult.Error);

        return await TryBuildDocxPlanWithLocalModelAsync(
            prompt,
            planContext,
            request.Model,
            ct);
    }

    private async Task<DocxPlanGenerationResult> TryBuildDocxPlanWithExternalAiAsync(
        string prompt,
        string planContext,
        ContractMergeRequest request,
        CancellationToken ct)
    {
        try
        {
            using var timeoutScope = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutScope.CancelAfter(TimeSpan.FromSeconds(_providerTimeoutSeconds));

            var response = await aiGatewayService.GenerateAsync(
                new AiGenerateRequest
                {
                    Task = "contract_merge_docx_plan",
                    Prompt = prompt,
                    Context = planContext,
                    Provider = request.ExternalProvider,
                    Model = request.ExternalModel,
                    ForceSpanish = request.ForceSpanish,
                    MaxTokens = 4096,
                    SystemInstruction = DocxPlanSystemInstruction
                },
                timeoutScope.Token);

            if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
            {
                return DocxPlanGenerationResult.FromError(
                    "external",
                    response.Error ?? "El proveedor externo no devolvio un plan util.");
            }

            var plan = JsonSanitizer.TryExtractJson<DocxMergePlan>(response.Text);
            if (plan is null)
            {
                return DocxPlanGenerationResult.FromError(
                    "external",
                    "El proveedor externo devolvio una respuesta que no pudo parsearse como JSON.");
            }

            return DocxPlanGenerationResult.FromSuccess("external", plan, response.Text);
        }
        catch (Exception ex)
        {
            return DocxPlanGenerationResult.FromError("external", ex.Message);
        }
    }

    private async Task<DocxPlanGenerationResult> TryBuildDocxPlanWithLocalModelAsync(
        string prompt,
        string planContext,
        string model,
        CancellationToken ct)
    {
        try
        {
            var userPrompt = BuildDocxPlanUserPrompt(prompt, planContext);

            var plan = await llm.GenerateAsync<DocxMergePlan>(
                DocxPlanSystemInstruction,
                userPrompt,
                model,
                ct,
                maxPredict: _localMaxPredict);

            if (plan is not null)
            {
                var rawJson = await llm.GenerateAsync(
                    DocxPlanSystemInstruction,
                    userPrompt,
                    model,
                    requireJson: true,
                    ct,
                    maxPredict: _localMaxPredict);

                return DocxPlanGenerationResult.FromSuccess("local", plan, rawJson);
            }

            var rawText = await llm.GenerateAsync(
                DocxPlanSystemInstruction,
                userPrompt,
                model,
                requireJson: true,
                ct,
                maxPredict: _localMaxPredict);

            plan = JsonSanitizer.TryExtractJson<DocxMergePlan>(rawText);

            if (plan is null)
            {
                logger.LogWarning(
                    "Local DOCX merge plan could not be parsed as JSON. Raw preview: {Preview}",
                    rawText[..Math.Min(rawText.Length, 500)]);

                return DocxPlanGenerationResult.FromError(
                    "local",
                    "El modelo local no devolvio un JSON valido para el plan de insercion.");
            }

            return DocxPlanGenerationResult.FromSuccess("local", plan, rawText);
        }
        catch (Exception ex)
        {
            return DocxPlanGenerationResult.FromError("local", ex.Message);
        }
    }

    private async Task<ProviderExecutionResult> ReviewMarkdownDraftAsync(
        string prompt,
        string context,
        string draftMarkdown,
        bool forceSpanish,
        string? externalProvider,
        string? externalModel,
        CancellationToken ct)
    {
        if (!aiOptionsMonitor.CurrentValue.UseExternalProviders)
        {
            logger.LogInformation("External providers are disabled. Skipping external Markdown review and keeping the local draft.");
            return ProviderExecutionResult.FromError("External providers are disabled.");
        }

        try
        {
            using var timeoutScope = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutScope.CancelAfter(TimeSpan.FromSeconds(_providerTimeoutSeconds));

            var reviewContext = new StringBuilder()
                .AppendLine("INSTRUCCION ORIGINAL DEL USUARIO")
                .AppendLine(prompt)
                .AppendLine()
                .AppendLine("CONTEXTO DOCUMENTAL")
                .AppendLine(context)
                .ToString()
                .Trim();

            var response = await aiGatewayService.GenerateAsync(
                new AiGenerateRequest
                {
                    Task = "contract_merge_markdown_review",
                    Prompt = draftMarkdown,
                    Context = reviewContext,
                    Provider = externalProvider,
                    Model = externalModel,
                    ForceSpanish = forceSpanish,
                    Metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mode"] = "review"
                    },
                    SystemInstruction = ExternalReviewSystemInstruction
                },
                timeoutScope.Token);

            if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
            {
                return ProviderExecutionResult.FromError(
                    response.Error ?? "El proveedor externo no devolvio una revision util.");
            }

            var reviewedMarkdown = NormalizeMarkdownOutput(response.Text);

            logger.LogInformation("External contract merge markdown review completed.");
            return ProviderExecutionResult.FromSuccess(reviewedMarkdown);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "External contract merge markdown review exceeded the timeout of {TimeoutSeconds} seconds.",
                _providerTimeoutSeconds);
            return ProviderExecutionResult.FromError(
                $"El proveedor externo excedio el tiempo limite de {_providerTimeoutSeconds} segundos.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External contract merge markdown review failed.");
            return ProviderExecutionResult.FromError(ex.Message);
        }
    }

    private string SelectPrimaryAnswer(string localDraft, ProviderExecutionResult reviewedResult)
    {
        if (!reviewedResult.Success || string.IsNullOrWhiteSpace(reviewedResult.Text))
            return localDraft;

        if (!HasCompletionMarker(reviewedResult.Text))
        {
            logger.LogWarning("External reviewed draft was rejected because it is incomplete.");
            return localDraft;
        }

        var localStructure = CountStructureSignals(localDraft);
        var reviewedStructure = CountStructureSignals(reviewedResult.Text);
        var minAcceptedLength = (int)Math.Round(localDraft.Length * 0.55);

        if (reviewedResult.Text.Length < minAcceptedLength && reviewedStructure < Math.Max(2, localStructure / 2))
        {
            logger.LogWarning(
                "External reviewed draft was rejected because it is disproportionately shorter than the local draft. Local length: {LocalLength}. Reviewed length: {ReviewedLength}.",
                localDraft.Length,
                reviewedResult.Text.Length);
            return localDraft;
        }

        return reviewedResult.Text;
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
        sb.AppendLine(baseDocument.LocalExcerpt);
        sb.AppendLine();
        sb.AppendLine("=== DOCUMENTOS FUENTE PARA INTEGRAR ===");

        foreach (var document in sourceDocuments)
        {
            sb.AppendLine($"[Archivo: {document.FileName}]");
            sb.AppendLine(document.LocalExcerpt);
            sb.AppendLine();
        }

        sb.AppendLine("=== INSTRUCCION OBLIGATORIA ===");
        sb.AppendLine(
            "Devuelve un borrador contractual consolidado en Markdown valido. " +
            "Integra las clausulas aplicables dentro del documento base y, si detectas conflictos, " +
            "senalalos brevemente al final bajo un apartado 'Notas de revision'. " +
            $"El contrato debe quedar completo y terminar exactamente con la linea '{CompletionMarker}'. " +
            "No uses bloques de codigo ni texto fuera del documento.");

        return sb.ToString().Trim();
    }

    private static string BuildContinuationPrompt(string originalPrompt, string currentDraft)
    {
        return new StringBuilder()
            .AppendLine("=== INSTRUCCION ORIGINAL DEL USUARIO ===")
            .AppendLine(originalPrompt)
            .AppendLine()
            .AppendLine("=== BORRADOR ACTUAL INCOMPLETO ===")
            .AppendLine(currentDraft)
            .AppendLine()
            .AppendLine("=== TAREA ===")
            .AppendLine(
                $"Continua exactamente el contrato desde donde se corto. No repitas lo anterior. " +
                $"Debes completar el documento y terminar con la linea exacta '{CompletionMarker}'.")
            .ToString()
            .Trim();
    }

    private string BuildReviewContext(
        PreparedDocument baseDocument,
        IReadOnlyList<PreparedDocument> sourceDocuments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DOCUMENTO BASE (BORRADOR EXISTENTE)");
        sb.AppendLine($"Archivo: {baseDocument.FileName}");
        sb.AppendLine(baseDocument.ReviewExcerpt);
        sb.AppendLine();
        sb.AppendLine("DOCUMENTOS FUENTE PARA INTEGRAR");

        foreach (var document in sourceDocuments)
        {
            sb.AppendLine($"Archivo: {document.FileName}");
            sb.AppendLine(document.ReviewExcerpt);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private string BuildDocumentExcerpt(
        string text,
        string fileName,
        string contentType,
        string prompt,
        ExcerptBudget budget)
    {
        var normalizedText = text.Trim();

        if (normalizedText.Length <= budget.MaxChars)
            return normalizedText;

        var chunks = chunker.Chunk(
            normalizedText,
            new ChunkMetadata
            {
                DocumentName = fileName,
                DocumentType = contentType,
                Source = "contract-merge"
            });

        var selectedChunks = SelectRelevantChunks(
            chunks,
            prompt,
            budget.MaxChunks,
            includeLeadingChunks: budget.LeadingChunks);

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

    private ExcerptBudget ResolveExcerptBudget(bool isBaseDocument, bool forReview)
    {
        if (isBaseDocument)
        {
            return forReview
                ? new ExcerptBudget(_reviewBaseDocumentChars, _reviewBaseChunks, 2)
                : new ExcerptBudget(_localBaseDocumentChars, _localBaseChunks, 2);
        }

        return forReview
            ? new ExcerptBudget(_reviewSourceDocumentChars, _reviewSourceChunks, 1)
            : new ExcerptBudget(_localSourceDocumentChars, _localSourceChunks, 1);
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

        return leading
            .Select(item => item.Index)
            .Concat(scored.Select(item => item.Index))
            .Distinct()
            .Take(limit)
            .OrderBy(index => index)
            .Select(index => chunks[index])
            .ToList();
    }

    private static HashSet<string> Tokenize(string text)
    {
        return text
            .ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '?', '!', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 3)
            .ToHashSet();
    }

    private static bool HasCompletionMarker(string markdown)
    {
        return markdown.Contains(CompletionMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMarkdownOutput(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var normalized = markdown.Trim();

        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = normalized.IndexOf('\n');
            if (firstNewLine >= 0)
                normalized = normalized[(firstNewLine + 1)..];
        }

        if (normalized.EndsWith("```", StringComparison.Ordinal))
            normalized = normalized[..^3];

        return normalized.Trim();
    }

    private static string AppendContinuation(string existingDraft, string continuation)
    {
        if (string.IsNullOrWhiteSpace(continuation))
            return existingDraft;

        if (string.IsNullOrWhiteSpace(existingDraft))
            return continuation;

        var cleanedExisting = existingDraft.TrimEnd();
        var cleanedContinuation = continuation.TrimStart();
        var overlap = FindOverlapLength(cleanedExisting, cleanedContinuation, 1200);

        if (overlap > 0)
            cleanedContinuation = cleanedContinuation[overlap..].TrimStart();

        if (string.IsNullOrWhiteSpace(cleanedContinuation))
            return cleanedExisting;

        return $"{cleanedExisting}\n\n{cleanedContinuation}".Trim();
    }

    private static int FindOverlapLength(string existingDraft, string continuation, int maxWindow)
    {
        var maxOverlap = Math.Min(Math.Min(existingDraft.Length, continuation.Length), maxWindow);

        for (var length = maxOverlap; length >= 80; length--)
        {
            if (existingDraft.EndsWith(
                    continuation[..length],
                    StringComparison.Ordinal))
            {
                return length;
            }
        }

        return 0;
    }

    private static int CountStructureSignals(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return 0;

        return markdown.Split('\n')
            .Count(line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.StartsWith("#", StringComparison.Ordinal) ||
                       trimmed.StartsWith("ARTICULO", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.StartsWith("ARTÍCULO", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string RemoveCompletionMarker(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var markerIndex = markdown.IndexOf(CompletionMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
            markdown = markdown[..markerIndex];

        return markdown.Trim();
    }

    private static string BuildMergedFileName(string baseDocumentFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(baseDocumentFileName);
        return $"{baseName}-merged.docx";
    }

    private static string BuildDocxPlanContext(
        string prompt,
        IReadOnlyList<DocxBlockSummary> baseBlocks,
        string sourceContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== INSTRUCCION DEL USUARIO ===");
        sb.AppendLine(prompt);
        sb.AppendLine();
        sb.AppendLine("=== BLOQUES DEL DOCUMENTO BASE ===");

        foreach (var block in baseBlocks)
        {
            sb.AppendLine($"Id: {block.BlockId}");
            sb.AppendLine($"Secuencia: {block.Sequence}");
            sb.AppendLine($"Encabezado: {block.Heading}");
            sb.AppendLine($"Es bloque de firmas: {block.IsSignatureBlock}");
            sb.AppendLine("Extracto:");
            sb.AppendLine(block.Excerpt);
            sb.AppendLine();
        }

        sb.AppendLine("=== DOCUMENTOS FUENTE CON CLAUSULAS ===");
        sb.AppendLine(sourceContext);
        sb.AppendLine();
        sb.AppendLine("=== ANCLAS ESPECIALES DISPONIBLES ===");
        sb.AppendLine("- __before_signatures__");
        sb.AppendLine("- __document_end__");

        return sb.ToString().Trim();
    }

    private static string BuildDocxPlanUserPrompt(string prompt, string planContext)
    {
        return new StringBuilder()
            .AppendLine("Genera el plan de insercion de clausulas para el documento base.")
            .AppendLine()
            .AppendLine("INSTRUCCION ORIGINAL DEL USUARIO")
            .AppendLine(prompt)
            .AppendLine()
            .AppendLine("CONTEXTO DOCUMENTAL")
            .AppendLine(planContext)
            .AppendLine()
            .AppendLine("Devuelve solo JSON valido.")
            .ToString()
            .Trim();
    }

    private static string BuildDocxOperationSummary(IReadOnlyList<DocxMergeOperation> operations)
    {
        if (operations.Count == 0)
            return "No se detectaron nuevas clausulas para insertar; se devolvio el documento base sin modificaciones.";

        var lines = operations.Select((operation, index) =>
            $"{index + 1}. Insertar '{operation.Heading ?? "clausula sin encabezado"}' en {operation.TargetBlockId ?? operation.Placement ?? "posicion sugerida"}.");

        return "Operaciones aplicadas al documento base:\n" + string.Join("\n", lines);
    }

    private IReadOnlyList<DocxMergeOperation> SanitizeDocxOperations(
        IReadOnlyList<DocxMergeOperation> operations,
        IReadOnlyList<DocxBlockSummary> baseBlocks,
        IReadOnlyList<SourceClauseCandidate> sourceClauses)
    {
        var validTargetIds = baseBlocks
            .Select(block => block.BlockId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        validTargetIds.Add("__before_signatures__");
        validTargetIds.Add("__document_end__");

        var baseTextIndex = baseBlocks
            .Select(block => NormalizeComparisonText($"{block.Heading}\n{block.Excerpt}"))
            .ToList();

        var blockById = baseBlocks.ToDictionary(block => block.BlockId, StringComparer.OrdinalIgnoreCase);
        var clausesById = sourceClauses.ToDictionary(clause => clause.ClauseId, StringComparer.OrdinalIgnoreCase);
        var seenSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedClauseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sanitized = new List<DocxMergeOperation>();

        foreach (var operation in operations)
        {
            var normalized = NormalizeDocxOperation(
                operation,
                validTargetIds,
                blockById,
                clausesById,
                usedClauseIds);
            if (normalized is null)
                continue;

            var signature = BuildOperationSignature(normalized);
            if (!seenSignatures.Add(signature))
            {
                logger.LogInformation(
                    "Skipping duplicate DOCX merge operation with heading '{Heading}' targeting '{Target}'.",
                    normalized.Heading,
                    normalized.TargetBlockId);
                continue;
            }

            if (OperationAlreadyExistsInBase(normalized, baseTextIndex))
            {
                logger.LogInformation(
                    "Skipping DOCX merge operation because similar content already exists in the base document. Heading: '{Heading}'.",
                    normalized.Heading);
                continue;
            }

            sanitized.Add(normalized);
        }

        foreach (var clause in sourceClauses.Where(clause => !usedClauseIds.Contains(clause.ClauseId)))
        {
            var fallback = CreateFallbackDocxOperation(clause, blockById.Values, baseTextIndex);
            if (fallback is null)
                continue;

            var signature = BuildOperationSignature(fallback);
            if (!seenSignatures.Add(signature))
                continue;

            if (OperationAlreadyExistsInBase(fallback, baseTextIndex))
            {
                logger.LogInformation(
                    "Skipping fallback DOCX merge operation because similar content already exists in the base document. Clause: '{ClauseId}'.",
                    clause.ClauseId);
                continue;
            }

            usedClauseIds.Add(clause.ClauseId);
            sanitized.Add(fallback);
            logger.LogInformation(
                "Added fallback DOCX merge operation for source clause '{ClauseId}' targeting '{TargetBlockId}'.",
                clause.ClauseId,
                fallback.TargetBlockId);
        }

        return sanitized;
    }

    private DocxMergeOperation? CreateFallbackDocxOperation(
        SourceClauseCandidate clause,
        IEnumerable<DocxBlockSummary> availableBlocks,
        IReadOnlyList<string> baseTextIndex)
    {
        var inferredTarget = InferBestTargetBlock(clause, availableBlocks);
        if (inferredTarget is null)
        {
            logger.LogWarning(
                "Could not infer a DOCX target block for source clause '{ClauseId}'. No fallback operation will be created.",
                clause.ClauseId);
            return null;
        }

        var placement = DeterminePreferredPlacement(clause, inferredTarget);
        var heading = BuildOperationHeading(null, clause, inferredTarget, availableBlocks, placement);
        var paragraphs = StripRedundantClauseHeadingLead(
            SplitClauseBodyIntoParagraphs(ExtractClauseBody(clause.Text, clause.Label)),
            heading,
            clause.Label);

        var uniqueParagraphs = new List<string>();
        var seenParagraphs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var paragraph in paragraphs)
        {
            var paragraphSignature = NormalizeComparisonText(paragraph);
            if (paragraphSignature.Length == 0 || !seenParagraphs.Add(paragraphSignature))
                continue;

            uniqueParagraphs.Add(paragraph);
        }

        var combinedBody = string.Join("\n\n", uniqueParagraphs);
        if (!IsLikelyMeaningfulOperation(heading, combinedBody) ||
            !OperationMatchesSourceClause(heading, uniqueParagraphs, clause))
        {
            logger.LogWarning(
                "Discarding fallback DOCX merge operation for source clause '{ClauseId}' because the normalized content is not usable.",
                clause.ClauseId);
            return null;
        }

        var fallback = new DocxMergeOperation
        {
            TargetBlockId = inferredTarget.BlockId,
            Placement = placement,
            SourceClauseId = clause.ClauseId,
            Heading = heading,
            Content = uniqueParagraphs.Count == 0 ? null : string.Join("\n\n", uniqueParagraphs),
            Paragraphs = uniqueParagraphs,
            Reason = "fallback_inferred_clause_insertion"
        };

        if (OperationAlreadyExistsInBase(fallback, baseTextIndex))
            return null;

        return fallback;
    }

    private DocxMergeOperation? NormalizeDocxOperation(
        DocxMergeOperation operation,
        ISet<string> validTargetIds,
        IReadOnlyDictionary<string, DocxBlockSummary> blockById,
        IReadOnlyDictionary<string, SourceClauseCandidate> clausesById,
        ISet<string> usedClauseIds)
    {
        var targetBlockId = NormalizeTargetBlockId(operation.TargetBlockId, validTargetIds);
        var placement = NormalizeOptionalText(operation.Placement)?.ToLowerInvariant();
        var sourceClauseId = NormalizeOptionalText(operation.SourceClauseId);
        var heading = NormalizeOptionalText(operation.Heading);
        var reason = NormalizeOptionalText(operation.Reason);

        if (!string.IsNullOrWhiteSpace(targetBlockId) && !validTargetIds.Contains(targetBlockId))
        {
            logger.LogWarning(
                "Discarding DOCX merge operation because targetBlockId '{TargetBlockId}' is not valid.",
                targetBlockId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(targetBlockId) &&
            placement is not ("append_end" or "append-end" or "before_signatures" or "before-signatures"))
        {
            logger.LogWarning(
                "Discarding DOCX merge operation because it does not specify a valid target block or special anchor.");
            return null;
        }

        var matchedClause = ResolveSourceClause(operation, clausesById);
        if (matchedClause is null)
        {
            logger.LogWarning(
                "Discarding DOCX merge operation because it could not be matched to a source clause. Heading: '{Heading}'.",
                heading);
            return null;
        }

        sourceClauseId = matchedClause.ClauseId;

        if (!usedClauseIds.Add(sourceClauseId))
        {
            logger.LogInformation(
                "Skipping DOCX merge operation because source clause '{SourceClauseId}' was already used.",
                sourceClauseId);
            return null;
        }

        var targetBlock = !string.IsNullOrWhiteSpace(targetBlockId) && blockById.TryGetValue(targetBlockId, out var foundBlock)
            ? foundBlock
            : null;

        var inferredTarget = InferBestTargetBlock(matchedClause, blockById.Values);
        if (ShouldPreferInferredTarget(targetBlockId, targetBlock, inferredTarget))
        {
            targetBlockId = inferredTarget?.BlockId;
            targetBlock = inferredTarget;
            placement = targetBlock is null ? placement : "before";
        }

        placement = DeterminePreferredPlacement(matchedClause, targetBlock, placement);
        heading = BuildOperationHeading(heading, matchedClause, targetBlock, blockById.Values, placement);

        var paragraphs = StripRedundantClauseHeadingLead(
            SplitClauseBodyIntoParagraphs(ExtractClauseBody(matchedClause.Text, matchedClause.Label)),
            heading,
            matchedClause.Label);

        var uniqueParagraphs = new List<string>();
        var seenParagraphs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var paragraph in paragraphs)
        {
            var paragraphSignature = NormalizeComparisonText(paragraph);
            if (paragraphSignature.Length == 0 || !seenParagraphs.Add(paragraphSignature))
                continue;

            uniqueParagraphs.Add(paragraph);
        }

        var combinedBody = string.Join("\n\n", uniqueParagraphs);
        if (!IsLikelyMeaningfulOperation(heading, combinedBody))
        {
            logger.LogWarning(
                "Discarding low-quality DOCX merge operation. Heading: '{Heading}', BodyLength: {BodyLength}.",
                heading,
                combinedBody.Length);
            return null;
        }

        if (!OperationMatchesSourceClause(heading, uniqueParagraphs, matchedClause))
        {
            logger.LogWarning(
                "Discarding DOCX merge operation because its content does not match source clause '{SourceClauseId}'.",
                sourceClauseId);
            return null;
        }

        return new DocxMergeOperation
        {
            TargetBlockId = targetBlockId,
            Placement = placement,
            SourceClauseId = sourceClauseId,
            Heading = heading,
            Content = uniqueParagraphs.Count == 0 ? null : string.Join("\n\n", uniqueParagraphs),
            Paragraphs = uniqueParagraphs,
            Reason = reason
        };
    }

    private static string? NormalizeTargetBlockId(string? rawTargetBlockId, ISet<string> validTargetIds)
    {
        var normalized = NormalizeOptionalText(rawTargetBlockId);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (validTargetIds.Contains(normalized))
            return normalized;

        var candidates = normalized
            .Split(['|', ',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeOptionalText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && validTargetIds.Contains(candidate))
                return candidate;
        }

        var specialAnchor = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate, "__before_signatures__", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "__document_end__", StringComparison.OrdinalIgnoreCase));

        return specialAnchor;
    }

    private static bool OperationAlreadyExistsInBase(
        DocxMergeOperation operation,
        IReadOnlyList<string> baseTextIndex)
    {
        var heading = NormalizeComparisonText(operation.Heading);
        var body = NormalizeComparisonText(string.Join("\n", operation.Paragraphs));
        var bodyNeedle = body.Length > 180 ? body[..180] : body;

        if (bodyNeedle.Length < 80)
            return false;

        return baseTextIndex.Any(baseText =>
            (!string.IsNullOrWhiteSpace(heading) && baseText.Contains(heading, StringComparison.Ordinal)) &&
            baseText.Contains(bodyNeedle, StringComparison.Ordinal));
    }

    private static string BuildOperationSignature(DocxMergeOperation operation)
    {
        return string.Join(
            "|",
            NormalizeComparisonText(operation.TargetBlockId),
            NormalizeComparisonText(operation.Placement),
            NormalizeComparisonText(operation.SourceClauseId),
            NormalizeComparisonText(operation.Heading),
            NormalizeComparisonText(string.Join("\n", operation.Paragraphs)));
    }

    private static bool ShouldPreferInferredTarget(
        string? targetBlockId,
        DocxBlockSummary? explicitTarget,
        DocxBlockSummary? inferredTarget)
    {
        if (inferredTarget is null)
            return false;

        if (string.IsNullOrWhiteSpace(targetBlockId))
            return true;

        if (string.Equals(targetBlockId, "__document_end__", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetBlockId, "__before_signatures__", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return explicitTarget is null;
    }

    private static DocxBlockSummary? InferBestTargetBlock(
        SourceClauseCandidate clause,
        IEnumerable<DocxBlockSummary> blocks)
    {
        DocxBlockSummary? bestBlock = null;
        var bestScore = 0d;

        foreach (var block in blocks)
        {
            if (block.IsSignatureBlock)
                continue;

            var score = ScoreClauseAgainstBlock(clause, block);
            if (score > bestScore)
            {
                bestScore = score;
                bestBlock = block;
            }
        }

        return bestScore >= 0.12 ? bestBlock : null;
    }

    private static double ScoreClauseAgainstBlock(SourceClauseCandidate clause, DocxBlockSummary block)
    {
        var clauseTopic = NormalizeComparisonText($"{clause.Label}\n{clause.Text}");
        var blockTopic = NormalizeComparisonText($"{block.Heading}\n{block.Excerpt}");

        if (string.IsNullOrWhiteSpace(clauseTopic) || string.IsNullOrWhiteSpace(blockTopic))
            return 0;

        var labelScore = ComputeClauseMatchScore(
            NormalizeComparisonText(clause.Label),
            NormalizeComparisonText(block.Heading));
        var bodyScore = ComputeClauseMatchScore(clauseTopic, blockTopic);

        var score = (labelScore * 0.6) + bodyScore;

        if (clauseTopic.Contains("seguro", StringComparison.OrdinalIgnoreCase) &&
            blockTopic.Contains("seguro", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.35;
        }

        if (clauseTopic.Contains("pago", StringComparison.OrdinalIgnoreCase) &&
            blockTopic.Contains("pago", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.25;
        }

        if (clauseTopic.Contains("vigencia", StringComparison.OrdinalIgnoreCase) &&
            blockTopic.Contains("vigencia", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.25;
        }

        if ((clauseTopic.Contains("almacen", StringComparison.OrdinalIgnoreCase) ||
             clauseTopic.Contains("almacenaje", StringComparison.OrdinalIgnoreCase) ||
             clauseTopic.Contains("nave", StringComparison.OrdinalIgnoreCase) ||
             clauseTopic.Contains("instalacion", StringComparison.OrdinalIgnoreCase)) &&
            (blockTopic.Contains("almacen", StringComparison.OrdinalIgnoreCase) ||
             blockTopic.Contains("almacenamiento", StringComparison.OrdinalIgnoreCase) ||
             blockTopic.Contains("nave", StringComparison.OrdinalIgnoreCase) ||
             blockTopic.Contains("servicio", StringComparison.OrdinalIgnoreCase) ||
             blockTopic.Contains("instalacion", StringComparison.OrdinalIgnoreCase)))
        {
            score += 0.35;
        }

        if ((clauseTopic.Contains("descripcion", StringComparison.OrdinalIgnoreCase) ||
             clauseTopic.Contains("ubicado", StringComparison.OrdinalIgnoreCase) ||
             clauseTopic.Contains("metros", StringComparison.OrdinalIgnoreCase)) &&
            (blockTopic.Contains("descripcion", StringComparison.OrdinalIgnoreCase) ||
             blockTopic.Contains("servicio", StringComparison.OrdinalIgnoreCase) ||
             blockTopic.Contains("almacenamiento", StringComparison.OrdinalIgnoreCase)))
        {
            score += 0.2;
        }

        return score;
    }

    private static string DeterminePreferredPlacement(
        SourceClauseCandidate clause,
        DocxBlockSummary? targetBlock,
        string? requestedPlacement = null)
    {
        if (targetBlock is null)
        {
            return string.IsNullOrWhiteSpace(requestedPlacement)
                ? "append_end"
                : requestedPlacement;
        }

        if (!string.IsNullOrWhiteSpace(requestedPlacement))
        {
            if (ShouldUseParagraphHeadingForClause(clause, targetBlock))
                return "before";

            return requestedPlacement;
        }

        return ShouldUseParagraphHeadingForClause(clause, targetBlock)
            ? "before"
            : "after";
    }

    private static bool IsLikelyMeaningfulOperation(string? heading, string body)
    {
        var normalizedHeading = NormalizeOptionalText(heading) ?? string.Empty;
        var normalizedBody = NormalizeOptionalText(body) ?? string.Empty;
        var bodyWords = normalizedBody
            .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Length;

        if (normalizedBody.Length >= 80 && bodyWords >= 12)
            return true;

        if (!string.IsNullOrWhiteSpace(normalizedHeading) &&
            normalizedBody.Length >= 40 &&
            bodyWords >= 6)
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> ExtractOperationParagraphs(DocxMergeOperation operation)
    {
        if (operation.Paragraphs.Count > 0)
            return operation.Paragraphs;

        if (string.IsNullOrWhiteSpace(operation.Content))
            return [];

        return operation.Content
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph))
            .ToList();
    }

    private IReadOnlyList<SourceClauseCandidate> ExtractSourceClauseCandidates(
        string extractedText,
        string fileName,
        string contentType,
        byte[]? docxBytes = null)
    {
        var clauses = new List<SourceClauseCandidate>();
        var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextSequence = 1;
        var tableDerivedClauseCount = 0;

        if (docxBytes is not null)
        {
            foreach (var clause in ExtractSourceClauseCandidatesFromDocxTables(docxBytes, fileName))
            {
                AddExpandedClauseCandidates(
                    clauses,
                    seenTexts,
                    ref nextSequence,
                    fileName,
                    clause.Label,
                    clause.Text);
                tableDerivedClauseCount = clauses.Count;
            }
        }

        if (tableDerivedClauseCount > 0)
        {
            logger.LogInformation(
                "Using {Count} table-derived source clause candidate(s) for '{FileName}' and skipping chunk-based clause recombination.",
                tableDerivedClauseCount,
                fileName);
            return clauses;
        }

        var chunks = chunker.Chunk(
            extractedText.Trim(),
            new ChunkMetadata
            {
                DocumentName = fileName,
                DocumentType = contentType,
                Source = "contract-merge-source-clauses"
            });

        foreach (var chunk in chunks)
        {
            var text = NormalizeOptionalText(chunk.Text);
            if (string.IsNullOrWhiteSpace(text) || !LooksLikeClauseCandidate(text, chunk.Metadata))
                continue;

            var label = NormalizeOptionalText(chunk.Metadata.Article)
                ?? NormalizeOptionalText(chunk.Metadata.Section)
                ?? TryInferClauseLabelFromText(text)
                ?? $"CLAUSULA {nextSequence}";

            AddExpandedClauseCandidates(
                clauses,
                seenTexts,
                ref nextSequence,
                fileName,
                label,
                text);
        }

        if (clauses.Count == 0)
            logger.LogWarning("No structured clause candidates were detected in source file '{FileName}'.", fileName);

        return clauses;
    }

    private IEnumerable<SourceClauseCandidate> ExtractSourceClauseCandidatesFromDocxTables(
        byte[] docxBytes,
        string fileName)
    {
        using var stream = new MemoryStream(docxBytes, writable: false);
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
            yield break;

        var sequence = 1;
        foreach (var table in body.Descendants<Table>())
        {
            foreach (var row in table.Elements<TableRow>())
            {
                var cells = row.Elements<TableCell>()
                    .Select(cell => NormalizeOptionalText(cell.InnerText))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => text!)
                    .Where(text => !SourceClausePlaceholderPattern.IsMatch(text))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (cells.Count == 0)
                    continue;

                var bodyCell = cells
                    .OrderByDescending(cell => cell.Length)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(bodyCell))
                    continue;

                if (!LooksLikeLooseClauseText(bodyCell))
                    continue;

                var labelCell = cells
                    .FirstOrDefault(cell =>
                        !string.Equals(cell, bodyCell, StringComparison.OrdinalIgnoreCase) &&
                        cell.Length <= 180);

                if (string.IsNullOrWhiteSpace(labelCell) &&
                    TrySplitSingleCellClause(bodyCell, out var inferredLabel, out var inferredBody))
                {
                    labelCell = inferredLabel;
                    bodyCell = inferredBody;
                }

                yield return new SourceClauseCandidate(
                    $"clause_{sequence:000}",
                    fileName,
                    labelCell ?? TryInferClauseLabelFromText(bodyCell) ?? $"CLAUSULA {sequence}",
                    bodyCell,
                    NormalizeComparisonText(bodyCell));

                sequence++;
            }
        }
    }

    private void AddExpandedClauseCandidates(
        List<SourceClauseCandidate> clauses,
        HashSet<string> seenTexts,
        ref int nextSequence,
        string fileName,
        string? preferredLabel,
        string rawText)
    {
        foreach (var segment in SplitStructuredClauseSegments(rawText))
        {
            var normalizedSegment = NormalizeOptionalText(segment);
            if (string.IsNullOrWhiteSpace(normalizedSegment))
                continue;

            if (!LooksLikeLooseClauseText(normalizedSegment) &&
                !LooksLikeExplicitStructuredClause(normalizedSegment))
            {
                continue;
            }

            var normalizedText = NormalizeComparisonText(normalizedSegment);
            if (!seenTexts.Add(normalizedText))
                continue;

            var label = ExtractLeadingClauseLabel(normalizedSegment)
                ?? NormalizeOptionalText(preferredLabel)
                ?? TryInferClauseLabelFromText(normalizedSegment)
                ?? $"CLAUSULA {nextSequence}";

            clauses.Add(new SourceClauseCandidate(
                $"clause_{nextSequence:000}",
                fileName,
                label,
                normalizedSegment,
                normalizedText));
            nextSequence++;
        }
    }

    private static IReadOnlyList<string> SplitStructuredClauseSegments(string text)
    {
        var normalizedText = NormalizeOptionalText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return [];

        var matches = ClauseSegmentStartPattern.Matches(normalizedText);
        if (matches.Count < 2)
            return [normalizedText];

        var segments = new List<string>();

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count
                ? matches[i + 1].Index
                : normalizedText.Length;

            var segment = normalizedText[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(segment))
                segments.Add(segment);
        }

        return segments.Count == 0 ? [normalizedText] : segments;
    }

    private static bool LooksLikeExplicitStructuredClause(string text)
    {
        var normalizedText = NormalizeOptionalText(text) ?? string.Empty;
        return normalizedText.StartsWith("PÁRRAFO", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.StartsWith("PARÁGRAFO", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.StartsWith("PARRAFO", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.StartsWith("NUMERAL", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.StartsWith("INCISO", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.StartsWith("LITERAL", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.StartsWith("ARTÍCULO", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.StartsWith("ARTICULO", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.StartsWith("CLÁUSULA", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.StartsWith("CLAUSULA", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractLeadingClauseLabel(string text)
    {
        var normalizedText = NormalizeOptionalText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return null;

        var line = normalizedText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .FirstOrDefault(part => part.Length > 0);

        if (string.IsNullOrWhiteSpace(line))
            return null;

        if (line.Length <= 180 && LooksLikeExplicitStructuredClause(line))
            return line;

        var colonIndex = line.IndexOf(':');
        if (colonIndex > 0)
        {
            var beforeColon = line[..colonIndex].Trim();
            if (beforeColon.Length <= 180 && LooksLikeExplicitStructuredClause(beforeColon))
                return beforeColon;
        }

        return null;
    }

    private static bool LooksLikeClauseCandidate(string text, ChunkMetadata metadata)
    {
        var wordCount = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < 12)
            return false;

        if (!string.IsNullOrWhiteSpace(metadata.Article) || !string.IsNullOrWhiteSpace(metadata.Section))
            return true;

        return text.Contains("clausula", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("articulo", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("párrafo", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("parrafo", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("inciso", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("numeral", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLooseClauseText(string text)
    {
        var normalized = NormalizeOptionalText(text) ?? string.Empty;
        if (SourceClausePlaceholderPattern.IsMatch(normalized))
            return false;

        var wordCount = normalized.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < 12)
            return false;

        if (normalized.Contains("el cliente", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("el proveedor", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("las partes", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("se obliga", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("acepta", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("deberá", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("debera", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return wordCount >= 20;
    }

    private static string? BuildOperationHeading(
        string? requestedHeading,
        SourceClauseCandidate clause,
        DocxBlockSummary? targetBlock,
        IEnumerable<DocxBlockSummary> availableBlocks,
        string? placement)
    {
        var normalizedRequestedHeading = NormalizeOptionalText(requestedHeading);
        var label = NormalizeOptionalText(clause.Label) ?? "Cláusula adicional";
        var shouldUseParagraphHeading = ShouldUseParagraphHeadingForClause(clause, targetBlock) &&
            placement is "before" or "inside_start" or "inside-start" or "prepend";

        if (ShouldSuppressStandaloneHeading(clause, targetBlock, shouldUseParagraphHeading))
            return null;

        if (!string.IsNullOrWhiteSpace(normalizedRequestedHeading) &&
            !shouldUseParagraphHeading &&
            LooksLikeStandaloneClauseHeading(normalizedRequestedHeading))
        {
            return normalizedRequestedHeading;
        }

        if (!shouldUseParagraphHeading)
            return label;


        var ordinal = DetermineInsertedParagraphOrdinal(targetBlock!, availableBlocks);
        return $"PÁRRAFO {ordinal}: {label}";
    }

    private static bool ShouldUseParagraphHeadingForClause(
        SourceClauseCandidate clause,
        DocxBlockSummary? targetBlock)
    {
        if (targetBlock is null || !IsMajorSectionHeadingText(targetBlock.Heading))
            return false;

        if (LooksLikeExplicitStructuredClause(clause.Label))
            return true;

        if (LooksLikeDescriptiveInlineClause(clause))
            return false;

        var normalized = NormalizeComparisonText($"{clause.Label}\n{clause.Text}");
        return normalized.Contains("seguro", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("poliza", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("subrogacion", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("responsabil", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("confidencial", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("vigencia", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("terminacion", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("indemne", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSuppressStandaloneHeading(
        SourceClauseCandidate clause,
        DocxBlockSummary? targetBlock,
        bool shouldUseParagraphHeading)
    {
        if (shouldUseParagraphHeading || targetBlock is null)
            return false;

        if (!IsMajorSectionHeadingText(targetBlock.Heading))
            return false;

        if (LooksLikeExplicitStructuredClause(clause.Label))
            return false;

        return LooksLikeDescriptiveInlineClause(clause);
    }

    private static bool LooksLikeDescriptiveInlineClause(SourceClauseCandidate clause)
    {
        var normalized = CanonicalizeHeadingText($"{clause.Label} {clause.Text}");
        return normalized.Contains("DESCRIPCION") ||
               normalized.Contains("NAVE") ||
               normalized.Contains("INSTALACION") ||
               normalized.Contains("UBICADO") ||
               normalized.Contains("METROS") ||
               normalized.Contains("AREA ");
    }

    private static string ExtractClauseBody(string text, string label)
    {
        var normalizedText = NormalizeOptionalText(text) ?? string.Empty;
        var normalizedLabel = NormalizeOptionalText(label) ?? string.Empty;

        if (normalizedLabel.Length == 0)
            return normalizedText;

        if (normalizedText.StartsWith(normalizedLabel, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = normalizedText[normalizedLabel.Length..].TrimStart(' ', ':', ';', '.', '-', '–', '—');
            if (!string.IsNullOrWhiteSpace(remainder))
                return remainder.Trim();
        }

        return normalizedText;
    }

    private static List<string> SplitClauseBodyIntoParagraphs(string body)
    {
        var normalizedBody = NormalizeOptionalText(body) ?? string.Empty;
        if (normalizedBody.Length == 0)
            return [];

        return normalizedBody
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph))
            .ToList();
    }

    private static string? TryInferClauseLabelFromText(string text)
    {
        var normalized = NormalizeOptionalText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (TrySplitSingleCellClause(normalized, out var inferredLabel, out _))
            return inferredLabel;

        var firstSentence = normalized
            .Split(['.', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .FirstOrDefault(part => part.Length > 0);

        if (string.IsNullOrWhiteSpace(firstSentence))
            return null;

        return firstSentence.Length <= 140
            ? firstSentence
            : firstSentence[..140].TrimEnd() + "...";
    }

    private static bool TrySplitSingleCellClause(string text, out string label, out string body)
    {
        label = string.Empty;
        body = text;

        var normalized = NormalizeOptionalText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var colonIndex = normalized.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= normalized.Length - 1)
            return false;

        var candidateLabel = normalized[..colonIndex].Trim();
        var candidateBody = normalized[(colonIndex + 1)..].Trim();

        if (candidateLabel.Length == 0 || candidateLabel.Length > 180 || candidateBody.Length < 20)
            return false;

        label = candidateLabel;
        body = candidateBody;
        return true;
    }

    private static bool LooksLikeStandaloneClauseHeading(string text)
    {
        var normalized = NormalizeOptionalText(text) ?? string.Empty;
        return normalized.Length > 0 && normalized.Length <= 220;
    }

    private static bool LooksLikeParagraphHeading(string text)
    {
        return ExtractParagraphOrdinalValuesFromText(text).Count > 0;
    }

    private static bool IsMajorSectionHeadingText(string text)
    {
        var normalizedCanonical = CanonicalizeHeadingText(text);
        return normalizedCanonical.StartsWith("ARTICULO ", StringComparison.Ordinal) ||
               normalizedCanonical.StartsWith("CLAUSULA ", StringComparison.Ordinal) ||
               normalizedCanonical.StartsWith("SECCION ", StringComparison.Ordinal) ||
               normalizedCanonical.StartsWith("CAPITULO ", StringComparison.Ordinal);

        var normalized = NormalizeOptionalText(text) ?? string.Empty;
        return normalized.StartsWith("ARTÍCULO", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("ARTICULO", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("CLÁUSULA", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("CLAUSULA", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("SECCIÓN", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("SECCION", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("CAPÍTULO", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("CAPITULO", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetermineInsertedParagraphOrdinal(
        DocxBlockSummary targetBlock,
        IEnumerable<DocxBlockSummary> availableBlocks)
    {
        var usedOrdinalValues = CollectSectionParagraphOrdinalValues(targetBlock, availableBlocks)
            .Where(value => value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        if (usedOrdinalValues.Count == 0)
        {
            var excerpt = NormalizeOptionalText(targetBlock.Excerpt) ?? string.Empty;
            usedOrdinalValues = ExtractParagraphOrdinalValuesFromText(excerpt);
        }

        if (usedOrdinalValues.Count == 0)
            return "I";

        var expectedValue = 1;
        foreach (var usedValue in usedOrdinalValues)
        {
            if (usedValue != expectedValue)
                return IntToRoman(expectedValue);

            expectedValue++;
        }

        return IntToRoman(expectedValue);
    }

    private static IEnumerable<int> CollectSectionParagraphOrdinalValues(
        DocxBlockSummary targetBlock,
        IEnumerable<DocxBlockSummary> availableBlocks)
    {
        var orderedBlocks = availableBlocks
            .OrderBy(block => block.Sequence)
            .ToList();

        var startIndex = orderedBlocks.FindIndex(block =>
            string.Equals(block.BlockId, targetBlock.BlockId, StringComparison.OrdinalIgnoreCase));

        if (startIndex < 0)
            return targetBlock.ParagraphOrdinalValues;

        var values = new List<int>();

        for (var i = startIndex; i < orderedBlocks.Count; i++)
        {
            var block = orderedBlocks[i];
            if (i > startIndex && IsMajorSectionHeadingText(block.Heading))
                break;

            if (block.ParagraphOrdinalValues.Count > 0)
                values.AddRange(block.ParagraphOrdinalValues);

            var excerpt = NormalizeOptionalText(block.Excerpt) ?? string.Empty;
            values.AddRange(ExtractParagraphOrdinalValuesFromText(excerpt));
        }

        return values;
    }

    private static List<int> ExtractParagraphOrdinalValuesFromText(string text)
    {
        var normalized = CanonicalizeHeadingText(text);
        return Regex.Matches(
                normalized,
                @"PARRAFO\s+([IVXLCDM]+)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)
            .Select(match => RomanToInt(match.Groups[1].Value.ToUpperInvariant()))
            .Where(value => value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
    }

    private static string CanonicalizeHeadingText(string? text)
    {
        var normalizedText = NormalizeOptionalText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return string.Empty;

        var decomposed = normalizedText.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb
            .ToString()
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant()
            .Trim();
    }

    private static string IntToRoman(int value)
    {
        if (value <= 0)
            return "I";

        var map = new (int Value, string Roman)[]
        {
            (1000, "M"),
            (900, "CM"),
            (500, "D"),
            (400, "CD"),
            (100, "C"),
            (90, "XC"),
            (50, "L"),
            (40, "XL"),
            (10, "X"),
            (9, "IX"),
            (5, "V"),
            (4, "IV"),
            (1, "I")
        };

        var sb = new StringBuilder();

        foreach (var item in map)
        {
            while (value >= item.Value)
            {
                sb.Append(item.Roman);
                value -= item.Value;
            }
        }

        return sb.Length == 0 ? "I" : sb.ToString();
    }

    private static int RomanToInt(string roman)
    {
        if (string.IsNullOrWhiteSpace(roman))
            return 0;

        var values = new Dictionary<char, int>
        {
            ['I'] = 1,
            ['V'] = 5,
            ['X'] = 10,
            ['L'] = 50,
            ['C'] = 100,
            ['D'] = 500,
            ['M'] = 1000
        };

        var total = 0;
        var previous = 0;

        for (var i = roman.Length - 1; i >= 0; i--)
        {
            if (!values.TryGetValue(roman[i], out var current))
                return 0;

            if (current < previous)
                total -= current;
            else
            {
                total += current;
                previous = current;
            }
        }

        return total;
    }

    private static bool HasExistingParagraphOrdinals(string text)
    {
        return ParagraphOrdinalPattern.IsMatch(NormalizeOptionalText(text) ?? string.Empty);
    }

    private static List<string> StripRedundantClauseHeadingLead(
        List<string> paragraphs,
        string? heading,
        string? sourceLabel)
    {
        if (paragraphs.Count == 0)
            return paragraphs;

        var firstParagraph = paragraphs[0];
        var normalizedFirstParagraph = NormalizeOptionalText(firstParagraph) ?? string.Empty;
        if (normalizedFirstParagraph.Length == 0)
            return paragraphs;

        var headingTopic = ExtractHeadingTopic(heading);
        var sourceTopic = NormalizeOptionalText(sourceLabel);

        var cleanedParagraph = StripLeadingTopic(normalizedFirstParagraph, headingTopic);
        cleanedParagraph = StripLeadingTopic(cleanedParagraph, sourceTopic);

        if (!string.Equals(cleanedParagraph, normalizedFirstParagraph, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(cleanedParagraph))
        {
            paragraphs[0] = cleanedParagraph;
        }

        return paragraphs;
    }

    private static string? ExtractHeadingTopic(string? heading)
    {
        var normalizedHeading = NormalizeOptionalText(heading);
        if (string.IsNullOrWhiteSpace(normalizedHeading))
            return null;

        var colonIndex = normalizedHeading.IndexOf(':');
        if (colonIndex >= 0 && colonIndex < normalizedHeading.Length - 1)
            return normalizedHeading[(colonIndex + 1)..].Trim();

        return normalizedHeading;
    }

    private static string StripLeadingTopic(string text, string? topic)
    {
        var normalizedTopic = NormalizeOptionalText(topic);
        if (string.IsNullOrWhiteSpace(normalizedTopic))
            return text;

        if (!text.StartsWith(normalizedTopic, StringComparison.OrdinalIgnoreCase))
            return text;

        var remainder = text[normalizedTopic.Length..].TrimStart(' ', ':', ';', '.', '-');
        return string.IsNullOrWhiteSpace(remainder) ? text : remainder.Trim();
    }

    private static string BuildSourceClauseCatalogContext(IReadOnlyList<SourceClauseCandidate> clauses)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== CATALOGO DE CLAUSULAS FUENTE ===");

        foreach (var clause in clauses)
        {
            sb.AppendLine($"Id: {clause.ClauseId}");
            sb.AppendLine($"Archivo: {clause.SourceFileName}");
            sb.AppendLine($"Etiqueta: {clause.Label}");
            sb.AppendLine("Texto:");
            sb.AppendLine(clause.Text);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static SourceClauseCandidate? ResolveSourceClause(
        DocxMergeOperation operation,
        IReadOnlyDictionary<string, SourceClauseCandidate> clausesById)
    {
        var sourceClauseId = NormalizeOptionalText(operation.SourceClauseId);
        if (!string.IsNullOrWhiteSpace(sourceClauseId) &&
            clausesById.TryGetValue(sourceClauseId, out var clauseById))
        {
            return clauseById;
        }

        var operationText = NormalizeComparisonText(
            string.Join("\n", new[] { operation.Heading, operation.Content, string.Join("\n", operation.Paragraphs) }));

        if (operationText.Length == 0)
            return null;

        SourceClauseCandidate? bestMatch = null;
        double bestScore = 0;

        foreach (var clause in clausesById.Values)
        {
            var score = ComputeClauseMatchScore(operationText, clause.NormalizedText);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = clause;
            }
        }

        return bestScore >= 0.58 ? bestMatch : null;
    }

    private static bool OperationMatchesSourceClause(
        string? heading,
        IReadOnlyList<string> paragraphs,
        SourceClauseCandidate clause)
    {
        var operationText = NormalizeComparisonText(
            string.Join("\n", new[] { heading ?? string.Empty, string.Join("\n", paragraphs) }));

        if (operationText.Length == 0)
            return false;

        var score = ComputeClauseMatchScore(operationText, clause.NormalizedText);
        return score >= 0.58;
    }

    private static double ComputeClauseMatchScore(string operationText, string clauseText)
    {
        if (string.IsNullOrWhiteSpace(operationText) || string.IsNullOrWhiteSpace(clauseText))
            return 0;

        if (clauseText.Contains(operationText, StringComparison.Ordinal) ||
            operationText.Contains(clauseText, StringComparison.Ordinal))
        {
            return 1;
        }

        var operationTokens = TokenizeComparisonText(operationText);
        var clauseTokens = TokenizeComparisonText(clauseText);

        if (operationTokens.Count == 0 || clauseTokens.Count == 0)
            return 0;

        var shared = operationTokens.Count(token => clauseTokens.Contains(token));
        return (double)shared / operationTokens.Count;
    }

    private static HashSet<string> TokenizeComparisonText(string text)
    {
        return text
            .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeComparisonText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return string.Join(
            " ",
            text.Trim()
                .ToLowerInvariant()
                .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? NormalizeOptionalText(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static async Task<byte[]> ReadAllBytesAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static int ResolvePositiveInt(string? rawValue, int fallback)
    {
        return int.TryParse(rawValue, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private sealed record PreparedDocument(
        int Index,
        string FileName,
        string ContentType,
        int CharacterCount,
        bool IsBaseDocument,
        string LocalExcerpt,
        string ReviewExcerpt);

    private sealed record ExcerptBudget(int MaxChars, int MaxChunks, int LeadingChunks);

    private sealed record IndexedChunk(DocumentChunk Chunk, int Index);

    private sealed record ScoredChunk(DocumentChunk Chunk, int Index, int Score);

    private sealed record MergeDocumentCandidate(
        int Index,
        string FileName,
        string ContentType,
        string ExtractedText,
        byte[]? DocxBytes,
        IReadOnlyList<DocxBlockSummary> BaseBlocks,
        IReadOnlyList<SourceClauseCandidate> SourceClauses,
        double ContractScore,
        double ClauseScore);

    private sealed record SourceClauseCandidate(
        string ClauseId,
        string SourceFileName,
        string Label,
        string Text,
        string NormalizedText);

    private sealed record DocxPlanGenerationResult(
        bool Success,
        string Provider,
        DocxMergePlan? Plan,
        string? RawText,
        string? Error)
    {
        public static DocxPlanGenerationResult FromSuccess(
            string provider,
            DocxMergePlan plan,
            string? rawText) => new(true, provider, plan, rawText, null);

        public static DocxPlanGenerationResult FromError(
            string provider,
            string error) => new(false, provider, null, null, error);
    }

    private sealed record ProviderExecutionResult(bool Success, string? Text, string? Error)
    {
        public static ProviderExecutionResult FromSuccess(string text) => new(true, text, null);
        public static ProviderExecutionResult FromError(string error) => new(false, null, error);
    }
}
