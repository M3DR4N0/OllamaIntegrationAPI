using System.Text;
using LlamaIntegrationAPI.Helpers;
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
    private const string CompletionMarker = "FIN DEL CONTRATO";

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
              "targetBlockId": "block_0004 | __before_signatures__ | __document_end__",
              "placement": "after | before | before_signatures | append_end",
              "heading": "titulo opcional de la nueva clausula",
              "content": "texto opcional en varios parrafos",
              "paragraphs": ["parrafo 1", "parrafo 2"],
              "reason": "motivo breve"
            }
          ]
        }

        REGLAS:
        - No reescribas todo el contrato.
        - Solo crea operaciones para clausulas nuevas o necesarias.
        - Usa exclusivamente informacion sustentada por los documentos.
        - Si una clausula ya existe en el documento base, no la dupliques.
        - Si vas a insertar un nuevo PARRAFO, NUMERAL, INCISO o LITERAL dentro de un ARTICULO o CLAUSULA ya existente, usa el targetBlockId de ese articulo o clausula y placement "before". El sistema lo insertara inmediatamente despues del encabezado del bloque y antes del contenido existente.
        - En esos casos, coloca el rotulo corto en "heading" (por ejemplo: "PARRAFO I: Cobertura...") y deja el texto explicativo dentro de "paragraphs" o "content".
        - No coloques el contenido antes del "heading" ni repitas el "heading" dentro de "paragraphs".
        - Si el lugar mas adecuado es antes de firmas, usa targetBlockId "__before_signatures__".
        - Si debe ir al final y no hay mejor ancla, usa "__document_end__" con placement "append_end".
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

        var baseFile = request.Files[request.BaseDocumentIndex];
        if (!DocxOriginalFormatMerger.LooksLikeDocx(baseFile))
        {
            throw new InvalidOperationException(
                "La preservacion de formato requiere que el documento base sea un archivo .docx.");
        }

        var baseDocxBytes = await ReadAllBytesAsync(baseFile, ct);
        var baseBlocks = DocxOriginalFormatMerger.Summarize(baseDocxBytes);

        if (baseBlocks.Count == 0)
        {
            throw new InvalidOperationException(
                "No se pudo identificar una estructura util dentro del documento base para insertar clausulas.");
        }

        var sourceContexts = new List<string>();

        for (var i = 0; i < request.Files.Count; i++)
        {
            if (i == request.BaseDocumentIndex)
                continue;

            var file = request.Files[i];
            var extractedText = await parser.ExtractTextAsync(file);

            if (string.IsNullOrWhiteSpace(extractedText))
                continue;

            var excerpt = BuildDocumentExcerpt(
                extractedText,
                file.FileName,
                file.ContentType,
                effectiveQuery,
                new ExcerptBudget(18000, 10, 1));

            sourceContexts.Add(
                $"[Archivo fuente: {file.FileName}]\n{excerpt}");
        }

        if (sourceContexts.Count == 0)
        {
            throw new InvalidOperationException(
                "No se pudo extraer texto de los documentos fuente con clausulas.");
        }

        var sourceContext = string.Join("\n\n", sourceContexts);
        var planResult = await BuildDocxMergePlanAsync(
            effectiveQuery,
            request,
            baseBlocks,
            sourceContext,
            ct);

        if (!planResult.Success || planResult.Plan is null)
        {
            throw new InvalidOperationException(
                "No se pudo construir el plan de insercion de clausulas para el documento Word. " +
                $"Detalle: {planResult.Error ?? "sin detalle"}.");
        }

        var operations = planResult.Plan.Operations
            .Where(operation => !string.IsNullOrWhiteSpace(operation.Content) ||
                                operation.Paragraphs.Count > 0 ||
                                !string.IsNullOrWhiteSpace(operation.Heading))
            .ToList();

        var mergedDocx = operations.Count == 0
            ? baseDocxBytes
            : DocxOriginalFormatMerger.ApplyOperations(baseDocxBytes, operations);

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
            BaseDocumentName = baseFile.FileName,
            WordDocument = mergedDocx,
            WordDocumentFileName = BuildMergedFileName(baseFile.FileName),
            WordDocumentContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        };
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
        string sourceContext,
        CancellationToken ct)
    {
        var planContext = BuildDocxPlanContext(prompt, baseBlocks, sourceContext);

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
            var plan = await llm.GenerateAsync<DocxMergePlan>(
                DocxPlanSystemInstruction,
                BuildDocxPlanUserPrompt(prompt, planContext),
                model,
                ct,
                maxPredict: 2600);

            if (plan is null)
            {
                return DocxPlanGenerationResult.FromError(
                    "local",
                    "El modelo local no devolvio un JSON valido para el plan de insercion.");
            }

            return DocxPlanGenerationResult.FromSuccess("local", plan, null);
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
