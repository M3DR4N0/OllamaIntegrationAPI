using System.Globalization;
using System.Text;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions DocxPlanCompatibilityJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string CompletionMarker = "FIN DEL CONTRATO";
    private static readonly Regex SourceClausePlaceholderPattern = new(
        @"secuencia[_\s-]*clausulas|^«.*»$|^<<.*>>$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ParagraphOrdinalPattern = new(
        @"P[ÁA]RRAFO\s+([IVXLCDM]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ListItemMarkerPattern = new(
        @"^\s*[\u2022\u25CF\u25AA\u25E6\u2023\u2043]\s*",
        RegexOptions.Compiled);

    private static readonly Regex ContractIndicatorPattern = new(
        @"\bcontrato\b|en fe de lo cual|las partes|objeto del contrato|vigencia|terminacion|firmas|por el cliente|por el proveedor",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClauseFileNamePattern = new(
        @"claus|adenda|anexo|anexos|terminos",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClauseSegmentStartPattern = new(
        @"(?i)(?=(?:^|[\r\n]+|\s{2,})(?:P[ÁA]RRAFO|NUMERAL|INCISO|LITERAL|ART[ÍI]CULO|CL[ÁA]USULA)\s+(?:[IVXLCDM]+|\d+|[A-Z]))",
        RegexOptions.Compiled);

    // These terms appear in almost every contract and are not useful for deciding
    // which substantive section is the best insertion point.
    private static readonly HashSet<string> GenericLegalTopicTokens =
    [
        "al", "aquel", "aquella", "aquellas", "aquellos", "caso", "casos", "como",
        "con", "conforme", "contrato", "cual", "cuales", "cuando", "debera", "deberan",
        "debe", "deben", "dicha", "dicho", "dichas", "dichos", "disposicion", "disposiciones",
        "efecto", "entre", "esta", "estas", "este", "estos", "forma", "las", "los", "mismo",
        "misma", "mismos", "mismas", "obliga", "obligacion", "obligaciones", "otra", "otras",
        "otro", "otros", "para", "parte", "partes", "podra", "podran", "por", "presente",
        "responsable", "responsabilidad", "se", "segun", "servicio", "servicios", "sin", "sobre",
        "sujeto", "toda", "todas", "todo", "todos", "una", "uno", "unos", "y"
    ];

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
    private readonly int _docxPlanMaxPredict = ResolvePositiveInt(
        configuration["ContractMerge:DocxPlanMaxPredict"], 900);
    private readonly int _continuationMaxPredict = ResolvePositiveInt(
        configuration["ContractMerge:ContinuationMaxPredict"], 2200);
    private readonly int _maxContinuationPasses = ResolvePositiveInt(
        configuration["ContractMerge:MaxContinuationPasses"], 3);
    private readonly int _docxPlanReviewPasses = ResolveNonNegativeInt(
        configuration["ContractMerge:DocxPlanReviewPasses"], 0);
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
              "action": "insert",
              "structure": "paragraph",
              "sourceParagraphIndexes": [1, 2],
              "confidence": 0.92,
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
        - Devuelve exactamente una decision por cada sourceClauseId del catalogo, incluso cuando la decision sea no insertar.
        - Usa "action": "insert" para integrar contenido nuevo y "action": "skip" cuando la clausula ya este totalmente cubierta o su insercion sea juridicamente improcedente.
        - No omitas ningun sourceClauseId. Una clausula omitida no se considera revisada.
        - Solo crea inserciones para clausulas nuevas o necesarias.
        - Usa exclusivamente informacion sustentada por el catalogo de clausulas detectadas.
        - Un mismo documento fuente puede contener varias clausulas independientes sobre temas distintos. Tratalas por separado y crea operaciones distintas cuando corresponda.
        - Si una clausula ya existe en el documento base, no la dupliques.
        - No repitas la misma clausula en multiples operaciones ni en multiples bloques.
        - No devuelvas fragmentos sueltos, textos aleatorios, ni lineas incompletas. Cada operacion debe representar una insercion juridica coherente y util.
        - Usa solo targetBlockId existentes en el mapa del documento base o las anclas especiales permitidas.
        - Cada operacion debe referenciar una clausula del catalogo usando sourceClauseId.
        - Usa "sourceParagraphIndexes" con indices basados en 1 para indicar que parrafos exactos de la clausula fuente deben insertarse. Si toda la clausula es nueva, incluye todos sus parrafos. Si solo una parte ya existe, incluye exclusivamente los parrafos no cubiertos. Para "skip", usa una lista vacia.
        - No incluyas las propiedades "content" ni "paragraphs" y no copies el texto fuente en la salida. El sistema recuperara literalmente los parrafos elegidos mediante sourceParagraphIndexes. Esto evita reescrituras, truncamientos e invenciones.
        - Usa "confidence" entre 0 y 1 para expresar la confianza juridica en la ubicacion y estructura propuestas.
        - Para cada operacion decide explicitamente la unidad juridica adecuada en "structure": "article", "clause", "section", "paragraph", "subclause", "list" o "prose".
        - No asumas que toda clausula debe ser un PARRAFO. Usa "paragraph" o "subclause" solo si el contenido depende juridicamente de un articulo o clausula existente; usa "article", "clause" o "section" si constituye una unidad autonoma; usa "list" si su contenido es una enumeracion; y usa "prose" si debe integrarse sin encabezado independiente.
        - No inventes nuevas clausulas ni redefines el texto juridico. Debes reutilizar el texto de la clausula fuente con cambios minimos de formato solamente cuando sean necesarios para insertarlo.
        - Nunca uses como destino un bloque anterior al primer ARTICULO, CLAUSULA o SECCION sustantiva. Esos bloques suelen contener solamente titulo, comparecencia, partes o preambulo.
        - Conserva la etiqueta de la clausula fuente como encabezado; no la reemplaces por un titulo inventado o por una reformulacion que cambie su alcance.
        - Antes de elegir targetBlockId, compara el tema juridico central de la clausula fuente con el encabezado y extracto de cada bloque del contrato base. Inserta la clausula en el bloque con mayor afinidad tematica.
        - Prioriza decidir: que clausula insertar y en que bloque insertarla. El sistema se encargara de ordenar encabezado y cuerpo al integrarlo.
        - Si vas a insertar un nuevo PARRAFO, NUMERAL, INCISO o LITERAL dentro de un ARTICULO o CLAUSULA ya existente, usa el targetBlockId de ese articulo o clausula y placement "before". El sistema lo insertara inmediatamente despues del encabezado del bloque y antes del contenido existente.
        - No es necesario devolver "heading": el sistema conservara la etiqueta fuente y asignara el ordinal correcto cuando corresponda.
        - No uses centrado, subrayado ni formato de titulo para una clausula insertada; el sistema heredara la alineacion y el estilo del cuerpo contractual cercano.
        - Si el lugar mas adecuado es antes de firmas, usa targetBlockId "__before_signatures__".
        - Si debe ir al final y no hay mejor ancla tematica razonable, usa "__document_end__" con placement "append_end".
        - Si no existe una coincidencia tematica razonable, usa "__before_signatures__"; nunca uses el inicio del documento como cajon de sastre.
        - Antes de responder, realiza internamente una segunda lectura critica de tu decision: verifica tema, duplicacion total o parcial, destino, estructura e indices. Devuelve unicamente el resultado final de esa autoauditoria.
        - Responde siempre en espanol.
        """;

    private const string DocxPlanReviewSystemInstruction = """
        Actua como abogado revisor senior independiente. Debes auditar y, cuando sea necesario, corregir un plan de integracion de clausulas en un contrato Word.

        No confirmes el plan por defecto. Para cada clausula fuente:
        - compara su materia, sujetos obligados, efecto juridico y alcance con todos los articulos del contrato base;
        - detecta duplicaciones totales o parciales;
        - decide si corresponde insertarla, omitirla o integrar solo determinados parrafos fuente;
        - elige la unidad juridica correcta: article, clause, section, paragraph, subclause, list o prose;
        - verifica que la ubicacion propuesta no sea el titulo, comparecencia, preambulo ni un articulo tematicamente ajeno;
        - para una disposicion autonoma, prefiere una unidad autonoma; para una regla subordinada, usa paragraph o subclause; para una enumeracion, usa list; para una continuacion sin titulo, usa prose.

        Debes devolver SOLO JSON valido con el mismo esquema del plan recibido.
        Reglas obligatorias:
        - exactamente una decision por cada sourceClauseId;
        - action debe ser insert o skip;
        - una decision skip no lleva targetBlockId y sourceParagraphIndexes debe ser [];
        - una decision insert debe usar un targetBlockId valido o una de las anclas especiales;
        - no incluyas content ni paragraphs porque el sistema copiara literalmente los parrafos indicados por sourceParagraphIndexes;
        - sourceParagraphIndexes usa indices basados en 1 y solo puede seleccionar parrafos existentes de esa clausula fuente;
        - no reescribas el texto juridico fuente ni inventes contenido;
        - confidence debe estar entre 0 y 1;
        - corrige cualquier ubicacion que no tenga relacion juridica directa con la clausula;
        - si no existe una ubicacion tematica razonable para una disposicion autonoma, usa __before_signatures__ en vez de insertarla en un articulo ajeno.
        - responde siempre en espanol.
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

        // A legally correct review may conclude that every source clause is
        // already covered. The endpoint must still honor its DOCX contract and
        // return a document, in that case an unchanged copy of the base file.
        var mergedDocx = operations.Count == 0
            ? baseDocxBytes.ToArray()
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
        var reviewedDecisions = new List<DocxMergeOperation>(sourceClauses.Count);
        var rawReviews = new StringBuilder();

        logger.LogInformation(
            "Building and reviewing DOCX merge decisions clause by clause for {ClauseCount} source clauses.",
            sourceClauses.Count);

        foreach (var sourceClause in sourceClauses)
        {
            ct.ThrowIfCancellationRequested();

            var singleClause = new[] { sourceClause };
            var sourceContext = BuildSourceClauseCatalogContext(singleClause);
            var planContext = BuildDocxPlanContext(prompt, baseBlocks, sourceContext, sourceClause);
            DocxPlanGenerationResult initialResult;

            if (!aiOptionsMonitor.CurrentValue.UseExternalProviders)
            {
                logger.LogInformation(
                    "External providers are disabled. Building local DOCX decision for source clause '{ClauseId}'.",
                    sourceClause.ClauseId);
                initialResult = await TryBuildDocxPlanWithLocalModelAsync(
                    prompt,
                    planContext,
                    request.Model,
                    sourceClause.ClauseId,
                    ct);
            }
            else
            {
                var externalResult = await TryBuildDocxPlanWithExternalAiAsync(
                    prompt,
                    planContext,
                    request,
                    ct);

                if (externalResult.Success)
                {
                    initialResult = externalResult;
                }
                else
                {
                    logger.LogWarning(
                        "External DOCX decision generation failed for source clause '{ClauseId}'. Falling back to local model. Error: {Error}",
                        sourceClause.ClauseId,
                        externalResult.Error);

                    initialResult = await TryBuildDocxPlanWithLocalModelAsync(
                        prompt,
                        planContext,
                        request.Model,
                        sourceClause.ClauseId,
                        ct);
                }
            }

            DocxPlanGenerationResult reviewedResult;

            var initialValidationError = initialResult.Error ?? string.Empty;
            var initialPlanIsValid = initialResult.Success &&
                                     initialResult.Plan is not null &&
                                     ValidateReviewedDocxPlan(
                                         initialResult.Plan,
                                         singleClause,
                                         baseBlocks,
                                         out initialValidationError);

            if (initialPlanIsValid && _docxPlanReviewPasses == 0)
            {
                // The model already performed the requested self-audit and the
                // deterministic contract checks passed. Avoid a redundant,
                // expensive Ollama call on CPU-only installations.
                reviewedResult = initialResult;
                logger.LogInformation(
                    "Accepted self-reviewed local DOCX decision for source clause '{ClauseId}' without an additional model call.",
                    sourceClause.ClauseId);
            }
            else if (_docxPlanReviewPasses > 0 &&
                     !string.IsNullOrWhiteSpace(initialResult.RawText))
            {
                if (!string.IsNullOrWhiteSpace(initialValidationError))
                {
                    logger.LogWarning(
                        "Initial DOCX decision for source clause '{ClauseId}' failed deterministic validation and requires model repair: {ValidationError}. Raw preview: {Preview}",
                        sourceClause.ClauseId,
                        initialValidationError,
                        (initialResult.RawText ?? string.Empty)[..Math.Min(initialResult.RawText?.Length ?? 0, 500)]);
                }

                reviewedResult = await TryReviewDocxPlanWithLocalModelAsync(
                    prompt,
                    planContext,
                    request.Model,
                    initialResult,
                    singleClause,
                    baseBlocks,
                    _docxPlanReviewPasses,
                    ct);
            }
            else
            {
                logger.LogWarning(
                    "Using deterministic safe DOCX decision for source clause '{ClauseId}' because the model decision was invalid: {ValidationError}. Raw preview: {Preview}",
                    sourceClause.ClauseId,
                    initialValidationError,
                    (initialResult.RawText ?? string.Empty)[..Math.Min(initialResult.RawText?.Length ?? 0, 500)]);
                reviewedResult = BuildDeterministicDocxDecision(
                    sourceClause,
                    baseBlocks,
                    initialResult.RawText);
            }

            if ((!reviewedResult.Success || reviewedResult.Plan is null) &&
                _docxPlanReviewPasses > 0)
            {
                logger.LogWarning(
                    "Model review failed for source clause '{ClauseId}'. Applying deterministic safe decision instead. Error: {Error}",
                    sourceClause.ClauseId,
                    reviewedResult.Error);
                reviewedResult = BuildDeterministicDocxDecision(
                    sourceClause,
                    baseBlocks,
                    reviewedResult.RawText ?? initialResult.RawText);
            }

            if (!reviewedResult.Success || reviewedResult.Plan is null)
                return reviewedResult;

            reviewedDecisions.Add(reviewedResult.Plan.Operations.Single());
            if (rawReviews.Length > 0)
                rawReviews.AppendLine().AppendLine();
            rawReviews
                .AppendLine($"/* {sourceClause.ClauseId} */")
                .Append(reviewedResult.RawText);

            logger.LogInformation(
                "Accepted reviewed DOCX decision for source clause '{ClauseId}' ({Completed}/{Total}).",
                sourceClause.ClauseId,
                reviewedDecisions.Count,
                sourceClauses.Count);
        }

        return DocxPlanGenerationResult.FromSuccess(
            "local",
            new DocxMergePlan
            {
                Summary = $"Plan juridico revisado clausula por clausula: {reviewedDecisions.Count} decisiones.",
                Operations = reviewedDecisions
            },
            rawReviews.ToString());
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

            var plan = NormalizeDocxPlanSchema(
                JsonSanitizer.TryExtractJson<DocxMergePlan>(response.Text));
            if (plan is null)
            {
                return DocxPlanGenerationResult.FromError(
                    "external",
                    "El proveedor externo devolvio una respuesta que no pudo parsearse como JSON.");
            }

            return DocxPlanGenerationResult.FromSuccess("external", plan, response.Text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        string expectedSourceClauseId,
        CancellationToken ct)
    {
        try
        {
            var userPrompt = BuildDocxPlanUserPrompt(prompt, planContext, expectedSourceClauseId);

            // Make exactly one Ollama request and parse the same response that
            // is retained as RawText for diagnostics and downstream auditing.
            var rawJson = await llm.GenerateAsync(
                DocxPlanSystemInstruction,
                userPrompt,
                model,
                requireJson: true,
                ct,
                maxPredict: _docxPlanMaxPredict);

            var plan = NormalizeDocxPlanSchema(
                JsonSanitizer.TryExtractJson<DocxMergePlan>(rawJson));

            if (plan is null)
            {
                logger.LogWarning(
                    "Local DOCX merge plan could not be parsed as JSON. Raw preview: {Preview}",
                    rawJson[..Math.Min(rawJson.Length, 500)]);

                return DocxPlanGenerationResult.FromError(
                    "local",
                    "El modelo local no devolvio un JSON valido para el plan de insercion.",
                    rawJson);
            }

            plan = BindPlanToSingleSourceClause(plan, expectedSourceClauseId);

            return DocxPlanGenerationResult.FromSuccess("local", plan, rawJson);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DocxPlanGenerationResult.FromError("local", ex.Message);
        }
    }

    private async Task<DocxPlanGenerationResult> TryReviewDocxPlanWithLocalModelAsync(
        string prompt,
        string planContext,
        string model,
        DocxPlanGenerationResult initialResult,
        IReadOnlyList<SourceClauseCandidate> sourceClauses,
        IReadOnlyList<DocxBlockSummary> baseBlocks,
        int requiredReviewPasses,
        CancellationToken ct)
    {
        var candidatePlanJson = initialResult.RawText ?? string.Empty;
        var validationFeedback = string.Empty;
        var lastError = "La revision juridica local no devolvio un plan util.";
        DocxPlanGenerationResult? lastAcceptedReview = null;
        var acceptedReviews = 0;
        var attempt = 0;
        var maxAttempts = requiredReviewPasses + 2;

        while (acceptedReviews < requiredReviewPasses && attempt < maxAttempts)
        {
            attempt++;
            try
            {
                var userPrompt = BuildDocxPlanReviewUserPrompt(
                    prompt,
                    planContext,
                    candidatePlanJson,
                    validationFeedback,
                    attempt,
                    acceptedReviews + 1,
                    requiredReviewPasses,
                    sourceClauses);

                var rawJson = await llm.GenerateAsync(
                    DocxPlanReviewSystemInstruction,
                    userPrompt,
                    model,
                    requireJson: true,
                    ct,
                    maxPredict: _docxPlanMaxPredict);

                var reviewedPlan = NormalizeDocxPlanSchema(
                    JsonSanitizer.TryExtractJson<DocxMergePlan>(rawJson));
                if (reviewedPlan is null)
                {
                    lastError = $"La revision juridica local {attempt} no devolvio JSON valido.";
                    validationFeedback = lastError;
                    logger.LogWarning(
                        "Local DOCX legal review attempt {Attempt} could not be parsed as JSON. Raw preview: {Preview}",
                        attempt,
                        rawJson[..Math.Min(rawJson.Length, 500)]);
                    continue;
                }

                if (sourceClauses.Count == 1)
                    reviewedPlan = BindPlanToSingleSourceClause(reviewedPlan, sourceClauses[0].ClauseId);

                if (!ValidateReviewedDocxPlan(reviewedPlan, sourceClauses, baseBlocks, out var validationError))
                {
                    lastError = $"La revision juridica local {attempt} produjo un plan incompleto o inseguro: {validationError}";
                    candidatePlanJson = rawJson;
                    validationFeedback = validationError;
                    logger.LogWarning(
                        "Local DOCX legal review attempt {Attempt} failed deterministic validation: {ValidationError}",
                        attempt,
                        validationError);
                    logger.LogWarning(
                        "Rejected local DOCX review raw preview: {Preview}",
                        rawJson[..Math.Min(rawJson.Length, 500)]);
                    continue;
                }

                acceptedReviews++;
                candidatePlanJson = rawJson;
                validationFeedback = string.Empty;
                lastAcceptedReview = DocxPlanGenerationResult.FromSuccess("local", reviewedPlan, rawJson);
                logger.LogInformation(
                    "Local DOCX legal review accepted pass {AcceptedPass}/{RequiredPasses} on attempt {Attempt}, with {DecisionCount} clause decisions.",
                    acceptedReviews,
                    requiredReviewPasses,
                    attempt,
                    reviewedPlan.Operations.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"La revision juridica local {attempt} fallo: {ex.Message}";
                validationFeedback = lastError;
                logger.LogWarning(ex, "Local DOCX legal review attempt {Attempt} failed.", attempt);
            }
        }

        if (acceptedReviews == requiredReviewPasses && lastAcceptedReview is not null)
            return lastAcceptedReview;

        // Returning the unreviewed proposal here would recreate the original
        // failure mode. Fail closed so the endpoint never emits a document
        // whose legal placement was not independently reviewed and validated.
        return DocxPlanGenerationResult.FromError("local", lastError);
    }

    private static DocxMergePlan BindPlanToSingleSourceClause(
        DocxMergePlan plan,
        string expectedSourceClauseId)
    {
        return new DocxMergePlan
        {
            Summary = plan.Summary,
            Operations = plan.Operations
                .Select(operation => operation with
                {
                    SourceClauseId = expectedSourceClauseId
                })
                .ToList()
        };
    }

    private static DocxPlanGenerationResult BuildDeterministicDocxDecision(
        SourceClauseCandidate sourceClause,
        IReadOnlyList<DocxBlockSummary> baseBlocks,
        string? rawModelText)
    {
        var sourceParagraphs = SplitClauseBodyIntoParagraphs(
            ExtractClauseBody(sourceClause.Text, sourceClause.Label));
        var uncoveredIndexes = Enumerable.Range(1, sourceParagraphs.Count)
            .Where(index => !SourceParagraphAlreadyCoveredInBase(
                sourceParagraphs[index - 1],
                baseBlocks))
            .ToList();

        DocxMergeOperation operation;
        if (uncoveredIndexes.Count == 0)
        {
            operation = new DocxMergeOperation
            {
                SourceClauseId = sourceClause.ClauseId,
                Action = "skip",
                Structure = "clause",
                SourceParagraphIndexes = [],
                Confidence = 1,
                Reason = "deterministic_all_source_paragraphs_already_covered"
            };
        }
        else
        {
            var targetBlock = FindDeterministicThematicTarget(sourceClause, baseBlocks) ??
                              InferBestTargetBlock(sourceClause, baseBlocks);
            if (targetBlock is not null && IsClearlyMismatchedDomain(sourceClause, targetBlock))
                targetBlock = null;

            var structure = InferDeterministicClauseStructure(sourceClause, targetBlock);
            var placement = DeterminePreferredPlacement(
                sourceClause,
                targetBlock,
                targetBlock is null ? "before_signatures" : null,
                structure);

            operation = new DocxMergeOperation
            {
                TargetBlockId = targetBlock?.BlockId ?? "__before_signatures__",
                Placement = placement,
                SourceClauseId = sourceClause.ClauseId,
                Action = "insert",
                Structure = structure,
                SourceParagraphIndexes = uncoveredIndexes,
                Confidence = targetBlock is null ? 0.70 : 0.85,
                Reason = targetBlock is null
                    ? "deterministic_safe_standalone_before_signatures"
                    : "deterministic_best_thematic_legal_section"
            };
        }

        return DocxPlanGenerationResult.FromSuccess(
            "deterministic",
            new DocxMergePlan
            {
                Summary = "Decision segura construida a partir de estructura, afinidad tematica y deduplicacion literal.",
                Operations = [operation]
            },
            rawModelText);
    }

    private static string InferDeterministicClauseStructure(
        SourceClauseCandidate sourceClause,
        DocxBlockSummary? targetBlock)
    {
        var paragraphs = SplitClauseBodyIntoParagraphs(
            ExtractClauseBody(sourceClause.Text, sourceClause.Label));
        if (paragraphs.Count(paragraph => ListItemMarkerPattern.IsMatch(paragraph)) >= 2)
            return "list";

        var label = CanonicalizeHeadingText(sourceClause.Label);
        if (label.StartsWith("ARTICULO", StringComparison.Ordinal))
            return "article";
        if (label.StartsWith("CLAUSULA", StringComparison.Ordinal))
            return "clause";
        if (label.StartsWith("SECCION", StringComparison.Ordinal))
            return "section";
        if (Regex.IsMatch(label, @"^(PARRAFO|NUMERAL|INCISO|LITERAL)\b"))
            return "paragraph";

        if (targetBlock is not null)
        {
            var targetTopic = CanonicalizeHeadingText($"{targetBlock.Heading} {targetBlock.Excerpt}");
            if (label.Contains("EXCLUSION", StringComparison.Ordinal) &&
                targetTopic.Contains("EXCLUSION", StringComparison.Ordinal))
            {
                return "paragraph";
            }
        }

        return "clause";
    }

    private static DocxBlockSummary? FindDeterministicThematicTarget(
        SourceClauseCandidate sourceClause,
        IReadOnlyList<DocxBlockSummary> baseBlocks)
    {
        var sourceLabel = CanonicalizeHeadingText(sourceClause.Label);
        var candidateBlocks = baseBlocks
            .OrderBy(block => block.Sequence)
            .ToList();

        static DocxBlockSummary? FindByHeading(
            IEnumerable<DocxBlockSummary> blocks,
            params string[] terms)
        {
            var blockList = blocks.ToList();
            var majorHeadingMatch = blockList.FirstOrDefault(block =>
                IsMajorSectionHeadingText(block.Heading) &&
                terms.Any(term => CanonicalizeHeadingText(block.Heading)
                    .Contains(term, StringComparison.Ordinal)));
            if (majorHeadingMatch is not null)
                return majorHeadingMatch;

            var headingMatch = blockList.FirstOrDefault(block =>
                terms.Any(term => CanonicalizeHeadingText(block.Heading)
                    .Contains(term, StringComparison.Ordinal)));
            if (headingMatch is not null)
                return headingMatch;

            return blockList.FirstOrDefault(block =>
                terms.Any(term => CanonicalizeHeadingText(block.Excerpt)
                    .Contains(term, StringComparison.Ordinal)));
        }

        if (sourceLabel.Contains("EXCLUSION", StringComparison.Ordinal))
            return FindByHeading(candidateBlocks, "EXCLUSION");

        if (sourceLabel.Contains("RESPONSABIL", StringComparison.Ordinal))
            return FindByHeading(candidateBlocks, "RESPONSABIL", "OBLIGACIONES DE EL PROVEEDOR");

        if (sourceLabel.Contains("CONFIDENCIAL", StringComparison.Ordinal))
            return FindByHeading(candidateBlocks, "CONFIDENCIAL");

        if (sourceLabel.Contains("SEGURO", StringComparison.Ordinal) ||
            sourceLabel.Contains("POLIZA", StringComparison.Ordinal))
        {
            return FindByHeading(candidateBlocks, "SEGURO", "POLIZA");
        }

        if (sourceLabel.Contains("PAGO", StringComparison.Ordinal) ||
            sourceLabel.Contains("FACTURA", StringComparison.Ordinal))
        {
            return FindByHeading(candidateBlocks, "PRECIO", "PAGO");
        }

        return null;
    }

    private static DocxMergePlan? NormalizeDocxPlanSchema(DocxMergePlan? plan)
    {
        if (plan is null)
            return null;

        var sourceOperations = plan.Operations.Count > 0
            ? plan.Operations
            : plan.ClausesAlias.Count > 0
                ? plan.ClausesAlias
                : plan.DecisionsAlias;

        if (sourceOperations.Count == 0 && plan.AdditionalProperties.Count > 0)
        {
            sourceOperations = plan.AdditionalProperties
                .Where(pair => pair.Value.ValueKind == JsonValueKind.Object)
                .Select(pair =>
                {
                    try
                    {
                        var operation = JsonSerializer.Deserialize<DocxMergeOperation>(
                            pair.Value.GetRawText(),
                            DocxPlanCompatibilityJsonOptions);
                        return operation is null
                            ? null
                            : operation with
                            {
                                SourceClauseId = NormalizeOptionalText(operation.SourceClauseId) ??
                                    NormalizeOptionalText(operation.SourceClauseIdAlias) ??
                                    NormalizeOptionalText(pair.Key)
                            };
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                })
                .Where(operation => operation is not null)
                .Cast<DocxMergeOperation>()
                .ToList();
        }

        return new DocxMergePlan
        {
            Summary = plan.Summary,
            Operations = sourceOperations
                .Select(operation => operation with
                {
                    TargetBlockId = NormalizeOptionalText(operation.TargetBlockId) ??
                        NormalizeOptionalText(operation.TargetBlockIdAlias),
                    SourceClauseId = NormalizeOptionalText(operation.SourceClauseId) ??
                        NormalizeOptionalText(operation.SourceClauseIdAlias),
                    Structure = NormalizeOptionalText(operation.Structure) ??
                        NormalizeOptionalText(operation.StructureAlias) ??
                        NormalizeOptionalText(operation.LegalUnitAlias),
                    SourceParagraphIndexes = operation.SourceParagraphIndexes.Count > 0
                        ? operation.SourceParagraphIndexes
                        : operation.SourceParagraphIndexesAlias
                })
                .ToList()
        };
    }

    private static string BuildDocxPlanReviewUserPrompt(
        string prompt,
        string planContext,
        string candidatePlanJson,
        string validationFeedback,
        int attempt,
        int reviewPass,
        int requiredReviewPasses,
        IReadOnlyList<SourceClauseCandidate> sourceClauses)
    {
        var requiredClauseIds = string.Join(", ", sourceClauses.Select(clause => clause.ClauseId));
        var sb = new StringBuilder()
            .AppendLine($"AUDITORIA JURIDICA DEL PLAN - REVISION {reviewPass} DE {requiredReviewPasses} - INTENTO {attempt}")
            .AppendLine($"SOURCECLAUSEID OBLIGATORIOS ({sourceClauses.Count}): {requiredClauseIds}")
            .AppendLine($"La matriz operations debe contener exactamente {sourceClauses.Count} decision(es).")
            .AppendLine()
            .AppendLine("INSTRUCCION ORIGINAL DEL USUARIO")
            .AppendLine(prompt)
            .AppendLine()
            .AppendLine("CONTEXTO DOCUMENTAL COMPLETO")
            .AppendLine(planContext)
            .AppendLine()
            .AppendLine("PLAN PROPUESTO QUE DEBES AUDITAR Y CORREGIR")
            .AppendLine(candidatePlanJson);

        if (!string.IsNullOrWhiteSpace(validationFeedback))
        {
            sb.AppendLine()
                .AppendLine("ERROR DE VALIDACION DEL INTENTO ANTERIOR")
                .AppendLine(validationFeedback)
                .AppendLine("Corrige expresamente este error en tu nueva respuesta.");
        }

        return sb
            .AppendLine()
            .AppendLine("Devuelve solo el JSON completo y corregido. No expliques tu respuesta fuera del JSON.")
            .ToString()
            .Trim();
    }

    private static bool ValidateReviewedDocxPlan(
        DocxMergePlan plan,
        IReadOnlyList<SourceClauseCandidate> sourceClauses,
        IReadOnlyList<DocxBlockSummary> baseBlocks,
        out string error)
    {
        var sourceById = sourceClauses.ToDictionary(clause => clause.ClauseId, StringComparer.OrdinalIgnoreCase);
        var validTargetIds = baseBlocks
            .Select(block => block.BlockId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        validTargetIds.Add("__before_signatures__");
        validTargetIds.Add("__document_end__");

        var groupedDecisions = plan.Operations
            .Where(operation => !string.IsNullOrWhiteSpace(operation.SourceClauseId))
            .GroupBy(operation => operation.SourceClauseId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var unknownIds = groupedDecisions.Keys
            .Where(id => !sourceById.ContainsKey(id))
            .ToList();
        if (unknownIds.Count > 0)
        {
            error = $"sourceClauseId desconocidos: {string.Join(", ", unknownIds)}.";
            return false;
        }

        var missingIds = sourceById.Keys
            .Where(id => !groupedDecisions.ContainsKey(id))
            .ToList();
        if (missingIds.Count > 0)
        {
            error = $"faltan decisiones para: {string.Join(", ", missingIds)}.";
            return false;
        }

        var duplicateIds = groupedDecisions
            .Where(pair => pair.Value.Count != 1)
            .Select(pair => pair.Key)
            .ToList();
        if (duplicateIds.Count > 0 || plan.Operations.Count != sourceClauses.Count)
        {
            error = $"debe existir exactamente una decision por clausula; duplicadas: {string.Join(", ", duplicateIds)}.";
            return false;
        }

        foreach (var sourceClause in sourceClauses)
        {
            var operation = groupedDecisions[sourceClause.ClauseId][0];
            var action = NormalizeOperationAction(operation.Action);
            if (action is not ("insert" or "skip"))
            {
                error = $"accion invalida para {sourceClause.ClauseId}: '{operation.Action}'.";
                return false;
            }

            if (operation.Confidence is null or < 0 or > 1)
            {
                error = $"confidence debe estar entre 0 y 1 para {sourceClause.ClauseId}.";
                return false;
            }

            if (action == "skip")
            {
                if (operation.SourceParagraphIndexes.Count > 0 ||
                    !string.IsNullOrWhiteSpace(operation.TargetBlockId) ||
                    !string.IsNullOrWhiteSpace(operation.Heading) ||
                    !string.IsNullOrWhiteSpace(operation.Content) ||
                    operation.Paragraphs.Count > 0)
                {
                    error = $"la decision skip de {sourceClause.ClauseId} contiene datos de insercion.";
                    return false;
                }

                continue;
            }

            var targetBlockId = NormalizeOptionalText(operation.TargetBlockId);
            if (string.IsNullOrWhiteSpace(targetBlockId) || !validTargetIds.Contains(targetBlockId))
            {
                error = $"targetBlockId invalido para {sourceClause.ClauseId}: '{operation.TargetBlockId}'.";
                return false;
            }

            var targetBlock = baseBlocks.FirstOrDefault(block =>
                string.Equals(block.BlockId, targetBlockId, StringComparison.OrdinalIgnoreCase));
            if (targetBlock is not null &&
                (targetBlock.IsSignatureBlock || IsIntroductoryBlock(targetBlock, baseBlocks)))
            {
                error = $"targetBlockId apunta a introduccion o firmas para {sourceClause.ClauseId}: '{targetBlockId}'.";
                return false;
            }

            if (NormalizeOperationStructure(operation.Structure) == "auto")
            {
                error = $"structure debe ser explicita para {sourceClause.ClauseId}.";
                return false;
            }

            var sourceParagraphCount = SplitClauseBodyIntoParagraphs(
                    ExtractClauseBody(sourceClause.Text, sourceClause.Label))
                .Count;
            var selectedIndexes = operation.SourceParagraphIndexes
                .Distinct()
                .ToList();

            if (selectedIndexes.Count == 0 ||
                selectedIndexes.Any(index => index < 1 || index > sourceParagraphCount))
            {
                error = $"sourceParagraphIndexes invalido para {sourceClause.ClauseId}; la clausula tiene {sourceParagraphCount} parrafos.";
                return false;
            }
        }

        error = string.Empty;
        return true;
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
        string sourceContext,
        SourceClauseCandidate? focusClause = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== INSTRUCCION DEL USUARIO ===");
        sb.AppendLine(prompt);
        sb.AppendLine();
        sb.AppendLine("=== BLOQUES DEL DOCUMENTO BASE ===");

        var firstSubstantiveSection = baseBlocks
            .OrderBy(block => block.Sequence)
            .FirstOrDefault(block => IsMajorSectionHeadingText(block.Heading));
        var detailedBlockIds = SelectDetailedPlanBlockIds(baseBlocks, focusClause);

        DocxBlockSummary? currentMajorSection = null;
        foreach (var block in baseBlocks.OrderBy(block => block.Sequence))
        {
            if (IsMajorSectionHeadingText(block.Heading))
                currentMajorSection = block;

            var isIntroductory = firstSubstantiveSection is not null &&
                                 block.Sequence < firstSubstantiveSection.Sequence;
            if (isIntroductory ||
                !detailedBlockIds.Contains(block.BlockId) && !block.IsSignatureBlock)
            {
                continue;
            }

            var parentBlockId = IsMajorSectionHeadingText(block.Heading)
                ? block.BlockId
                : currentMajorSection?.BlockId ?? "ninguno";
            var excerptBudget = IsMajorSectionHeadingText(block.Heading) ? 280 : 320;
            var compactExcerpt = BuildCompactPlanExcerpt(block.Excerpt, excerptBudget);
            var blockKind = isIntroductory
                ? "INTRO"
                : block.IsSignatureBlock
                    ? "FIRMA"
                    : IsMajorSectionHeadingText(block.Heading)
                        ? "PRINCIPAL"
                        : LooksLikeParagraphHeading(block.Heading)
                            ? "SUBORDINADO"
                            : "AUXILIAR";

            sb.AppendLine(
                $"{block.BlockId}|n={block.Sequence}|tipo={blockKind}|padre={parentBlockId}|encabezado={block.Heading}");
            if (detailedBlockIds.Contains(block.BlockId) &&
                !isIntroductory &&
                !block.IsSignatureBlock &&
                compactExcerpt.Length > 0)
            {
                sb.AppendLine($"  texto={compactExcerpt}");
            }
        }

        sb.AppendLine("=== DOCUMENTOS FUENTE CON CLAUSULAS ===");
        sb.AppendLine(sourceContext);
        sb.AppendLine();
        sb.AppendLine("=== ANCLAS ESPECIALES DISPONIBLES ===");
        sb.AppendLine("- __before_signatures__");
        sb.AppendLine("- __document_end__");

        return sb.ToString().Trim();
    }

    private static HashSet<string> SelectDetailedPlanBlockIds(
        IReadOnlyList<DocxBlockSummary> baseBlocks,
        SourceClauseCandidate? focusClause)
    {
        var selected = baseBlocks
            .Where(block => IsMajorSectionHeadingText(block.Heading))
            .Select(block => block.BlockId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (focusClause is null)
            return selected;

        foreach (var block in baseBlocks
                     .Where(block => !block.IsSignatureBlock)
                     .OrderByDescending(block => ScoreClauseAgainstBlock(focusClause, block))
                     .ThenBy(block => block.Sequence)
                     .Take(8))
        {
            selected.Add(block.BlockId);

            var parent = baseBlocks
                .Where(candidate =>
                    candidate.Sequence <= block.Sequence &&
                    IsMajorSectionHeadingText(candidate.Heading))
                .OrderByDescending(candidate => candidate.Sequence)
                .FirstOrDefault();
            if (parent is not null)
                selected.Add(parent.BlockId);
        }

        return selected;
    }

    private static string BuildCompactPlanExcerpt(string? excerpt, int maxLength)
    {
        var compact = Regex.Replace(
                NormalizeOptionalText(excerpt) ?? string.Empty,
                @"\s+",
                " ")
            .Trim();

        if (compact.Length <= maxLength)
            return compact;

        return compact[..maxLength].TrimEnd() + "...";
    }

    private static string DescribeLegalBlockKind(DocxBlockSummary block)
    {
        var canonicalHeading = CanonicalizeHeadingText(block.Heading);
        if (canonicalHeading.StartsWith("ARTICULO ", StringComparison.Ordinal))
            return "articulo principal";
        if (canonicalHeading.StartsWith("CLAUSULA ", StringComparison.Ordinal))
            return "clausula principal";
        if (canonicalHeading.StartsWith("SECCION ", StringComparison.Ordinal))
            return "seccion principal";
        if (canonicalHeading.StartsWith("CAPITULO ", StringComparison.Ordinal))
            return "capitulo principal";
        if (LooksLikeParagraphHeading(block.Heading))
            return "parrafo subordinado";
        if (block.IsSignatureBlock)
            return "firmas";

        return "bloque auxiliar o de texto";
    }

    private static string BuildDocxPlanUserPrompt(
        string prompt,
        string planContext,
        string expectedSourceClauseId)
    {
        return new StringBuilder()
            .AppendLine($"DECIDE UNICAMENTE {expectedSourceClauseId}.")
            .AppendLine("operations debe contener exactamente una decision y sourceClauseId debe ser exactamente el ID anterior.")
            .AppendLine("No uses ningun otro sourceClauseId.")
            .AppendLine("No incluyas content ni paragraphs. Devuelve un JSON breve.")
            .AppendLine("Genera el plan de insercion para esta unica clausula.")
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
        var reviewedClauseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sanitized = new List<DocxMergeOperation>();

        foreach (var operation in operations)
        {
            var normalized = NormalizeDocxOperation(
                operation,
                validTargetIds,
                blockById,
                clausesById,
                reviewedClauseIds);
            if (normalized is null)
            {
                throw new InvalidOperationException(
                    $"La decision revisada para la clausula '{operation.SourceClauseId ?? "sin id"}' no pudo normalizarse de forma segura.");
            }

            if (string.Equals(normalized.Action, "skip", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "The reviewed DOCX plan explicitly skipped source clause '{SourceClauseId}'. Reason: {Reason}",
                    normalized.SourceClauseId,
                    normalized.Reason);
                continue;
            }

            logger.LogInformation(
                "Accepted DOCX insertion for source clause '{SourceClauseId}': target={TargetBlockId}, placement={Placement}, structure={Structure}, paragraphs={ParagraphIndexes}.",
                normalized.SourceClauseId,
                normalized.TargetBlockId,
                normalized.Placement,
                normalized.Structure,
                string.Join(",", normalized.SourceParagraphIndexes));

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

        var clausesWithoutDecision = sourceClauses
            .Where(clause => !reviewedClauseIds.Contains(clause.ClauseId))
            .Select(clause => clause.ClauseId)
            .ToList();
        if (clausesWithoutDecision.Count > 0)
        {
            throw new InvalidOperationException(
                "El plan revisado no contiene una decision explicita para todas las clausulas fuente: " +
                string.Join(", ", clausesWithoutDecision));
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
                "Could not infer a DOCX target block for source clause '{ClauseId}'. Falling back to the position before signatures.",
                clause.ClauseId);
        }

        var placement = inferredTarget is null
            ? "before_signatures"
            : DeterminePreferredPlacement(clause, inferredTarget);
        var heading = BuildOperationHeading(
            clause,
            inferredTarget,
            availableBlocks,
            placement,
            "auto",
            null);
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
            TargetBlockId = inferredTarget?.BlockId ?? "__before_signatures__",
            Placement = placement,
            SourceClauseId = clause.ClauseId,
            Structure = "auto",
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
        ISet<string> reviewedClauseIds)
    {
        var action = NormalizeOperationAction(operation.Action);
        var structure = NormalizeOperationStructure(operation.Structure);
        var heading = NormalizeOptionalText(operation.Heading);
        var reason = NormalizeOptionalText(operation.Reason);

        var matchedClause = ResolveSourceClause(operation, clausesById);
        if (matchedClause is null)
        {
            logger.LogWarning(
                "Discarding DOCX merge operation because it could not be matched to a source clause. Heading: '{Heading}'.",
                heading);
            return null;
        }

        var sourceClauseId = matchedClause.ClauseId;

        if (!reviewedClauseIds.Add(sourceClauseId))
        {
            logger.LogInformation(
                "Discarding DOCX merge operation because source clause '{SourceClauseId}' already has a reviewed decision.",
                sourceClauseId);
            return null;
        }

        if (action == "skip")
        {
            return new DocxMergeOperation
            {
                SourceClauseId = sourceClauseId,
                Action = "skip",
                Structure = structure,
                SourceParagraphIndexes = [],
                Confidence = operation.Confidence,
                Reason = reason
            };
        }

        if (action != "insert")
        {
            logger.LogWarning(
                "Discarding DOCX merge operation because action '{Action}' is invalid for source clause '{SourceClauseId}'.",
                operation.Action,
                sourceClauseId);
            return null;
        }

        var sourceParagraphsForStructure = SplitClauseBodyIntoParagraphs(
            ExtractClauseBody(matchedClause.Text, matchedClause.Label));
        var sourceListItemCount = sourceParagraphsForStructure.Count(paragraph =>
            ListItemMarkerPattern.IsMatch(paragraph));
        if (sourceListItemCount >= 2 &&
            !LooksLikeExplicitStructuredClause(matchedClause.Label) &&
            IsParagraphStructure(structure))
        {
            logger.LogInformation(
                "Promoting paragraph structure to list for source clause '{SourceClauseId}' because the source contains {ListItemCount} list items.",
                sourceClauseId,
                sourceListItemCount);
            structure = "list";
        }

        var targetBlockId = NormalizeTargetBlockId(operation.TargetBlockId, validTargetIds);
        var placement = NormalizeOptionalText(operation.Placement)?.ToLowerInvariant();

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

        var targetBlock = !string.IsNullOrWhiteSpace(targetBlockId) && blockById.TryGetValue(targetBlockId, out var foundBlock)
            ? foundBlock
            : null;

        if (targetBlock is not null &&
            !IsMajorSectionHeadingText(targetBlock.Heading) &&
            (IsParagraphStructure(structure) || IsStandaloneStructure(structure)))
        {
            var parentSection = FindContainingMajorSection(targetBlock, blockById.Values);
            if (parentSection is not null)
            {
                logger.LogInformation(
                    "Promoting reviewed target '{OriginalTarget}' to parent legal section '{ParentTarget}' for structure '{Structure}'.",
                    targetBlock.BlockId,
                    parentSection.BlockId,
                    structure);
                targetBlock = parentSection;
                targetBlockId = parentSection.BlockId;
                placement = IsParagraphStructure(structure) ? "before" : "after";
            }
        }

        var deterministicThematicTarget = FindDeterministicThematicTarget(
            matchedClause,
            blockById.Values.OrderBy(block => block.Sequence).ToList());
        if (deterministicThematicTarget is not null &&
            !IsClearlyMismatchedDomain(matchedClause, deterministicThematicTarget))
        {
            if (!string.Equals(
                    targetBlockId,
                    deterministicThematicTarget.BlockId,
                    StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Replacing model target '{ModelTarget}' with direct legal-topic target '{ThematicTarget}' for source clause '{SourceClauseId}'.",
                    targetBlockId,
                    deterministicThematicTarget.BlockId,
                    sourceClauseId);
            }

            targetBlockId = deterministicThematicTarget.BlockId;
            targetBlock = deterministicThematicTarget;
            placement = IsParagraphStructure(structure) ? "before" : "after";
        }

        var inferredTarget = InferBestTargetBlock(matchedClause, blockById.Values);
        if (deterministicThematicTarget is null &&
            ShouldPreferInferredTarget(
                targetBlockId,
                targetBlock,
                inferredTarget,
                matchedClause,
                blockById.Values))
        {
            targetBlockId = inferredTarget?.BlockId;
            targetBlock = inferredTarget;
            placement = targetBlock is null ? placement : "before";
        }
        else if (targetBlock is not null &&
                 IsIntroductoryBlock(targetBlock, blockById.Values) &&
                 inferredTarget is null)
        {
            // Never place a new legal clause before the contract title/parties.
            // If no reliable thematic section exists, the least surprising safe
            // location is immediately before the signatures.
            targetBlockId = "__before_signatures__";
            targetBlock = null;
            placement = "before_signatures";
        }
        else if (targetBlock is not null &&
                 inferredTarget is null &&
                 deterministicThematicTarget is null &&
                 !string.Equals(
                     reason,
                     "deterministic_best_thematic_legal_section",
                     StringComparison.OrdinalIgnoreCase) &&
                 ScoreClauseAgainstBlock(matchedClause, targetBlock) < 0.08)
        {
            logger.LogWarning(
                "Replacing weak unrelated reviewed target '{TargetBlockId}' with the safe before-signatures anchor for source clause '{SourceClauseId}'.",
                targetBlock.BlockId,
                sourceClauseId);
            targetBlockId = "__before_signatures__";
            targetBlock = null;
            placement = "before_signatures";
        }

        if (targetBlock is not null && IsClearlyMismatchedDomain(matchedClause, targetBlock))
        {
            logger.LogWarning(
                "Replacing domain-mismatched target '{TargetBlockId}' with the safe before-signatures anchor for source clause '{SourceClauseId}'.",
                targetBlock.BlockId,
                sourceClauseId);
            targetBlockId = "__before_signatures__";
            targetBlock = null;
            placement = "before_signatures";
        }

        placement = DeterminePreferredPlacement(matchedClause, targetBlock, placement, structure);
        if (string.Equals(targetBlockId, "__before_signatures__", StringComparison.OrdinalIgnoreCase))
            placement = "before_signatures";
        else if (string.Equals(targetBlockId, "__document_end__", StringComparison.OrdinalIgnoreCase))
            placement = "append_end";
        heading = BuildOperationHeading(
            matchedClause,
            targetBlock,
            blockById.Values,
            placement,
            structure,
            operation.Heading);

        var allSourceParagraphs = SplitClauseBodyIntoParagraphs(
            ExtractClauseBody(matchedClause.Text, matchedClause.Label));
        var modelRequestedIndexes = operation.SourceParagraphIndexes
            .Distinct()
            .OrderBy(index => index)
            .ToList();
        if (modelRequestedIndexes.Any(index => index < 1 || index > allSourceParagraphs.Count))
        {
            logger.LogWarning(
                "Discarding DOCX merge operation because sourceParagraphIndexes is invalid for source clause '{SourceClauseId}'.",
                sourceClauseId);
            return null;
        }

        // The model decides legal placement and structure, but it must never
        // silently delete source terms. Start from every source paragraph and
        // remove only content that deterministic comparison proves is already
        // covered by the base contract.
        var requestedIndexes = Enumerable.Range(1, allSourceParagraphs.Count).ToList();
        if (modelRequestedIndexes.Count != requestedIndexes.Count)
        {
            logger.LogInformation(
                "Ignoring partial model paragraph selection for source clause '{SourceClauseId}' and preserving all {ParagraphCount} source paragraphs before deduplication.",
                sourceClauseId,
                requestedIndexes.Count);
        }

        var selectedIndexes = requestedIndexes
            .Where(index => !SourceParagraphAlreadyCoveredInBase(
                allSourceParagraphs[index - 1],
                blockById.Values))
            .ToList();
        if (selectedIndexes.Count != requestedIndexes.Count)
        {
            logger.LogInformation(
                "Removed {DuplicateCount} already-covered source paragraph(s) from clause '{SourceClauseId}'.",
                requestedIndexes.Count - selectedIndexes.Count,
                sourceClauseId);
        }

        if (selectedIndexes.Count == 0)
        {
            return new DocxMergeOperation
            {
                SourceClauseId = sourceClauseId,
                Action = "skip",
                Structure = structure,
                SourceParagraphIndexes = [],
                Confidence = operation.Confidence,
                Reason = "all_selected_source_paragraphs_already_covered"
            };
        }

        var paragraphs = StripRedundantClauseHeadingLead(
            selectedIndexes.Select(index => allSourceParagraphs[index - 1]).ToList(),
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

        return new DocxMergeOperation
        {
            TargetBlockId = targetBlockId,
            Placement = placement,
            SourceClauseId = sourceClauseId,
            Action = "insert",
            Structure = structure,
            SourceParagraphIndexes = selectedIndexes,
            Confidence = operation.Confidence,
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

    private static bool SourceParagraphAlreadyCoveredInBase(
        string sourceParagraph,
        IEnumerable<DocxBlockSummary> baseBlocks)
    {
        var sourceCanonical = CanonicalizeHeadingText(sourceParagraph);
        var sourceTokens = TokenizeCoverageText(sourceParagraph);
        if (sourceTokens.Count < 12)
            return false;

        foreach (var block in baseBlocks)
        {
            var baseText = $"{block.Heading} {block.Excerpt}";
            var baseCanonical = CanonicalizeHeadingText(baseText);
            if (sourceCanonical.Contains("LUCRO CESANTE", StringComparison.Ordinal) &&
                sourceCanonical.Contains("DANOS INDIRECTOS", StringComparison.Ordinal) &&
                (baseCanonical.Contains("LUCRO CESANTE", StringComparison.Ordinal) ||
                 baseCanonical.Contains("DANOS CONSECUENCIALES", StringComparison.Ordinal) ||
                 baseCanonical.Contains("DANOS INDIRECTOS", StringComparison.Ordinal)))
            {
                return true;
            }

            var baseTokens = TokenizeCoverageText(baseText);
            if (baseTokens.Count < 12)
                continue;

            var shared = sourceTokens.Count(token => baseTokens.Contains(token));
            var denominator = Math.Min(sourceTokens.Count, baseTokens.Count);
            if (denominator >= 12 && (double)shared / denominator >= 0.62)
                return true;
        }

        return false;
    }

    private static HashSet<string> TokenizeCoverageText(string? text)
    {
        return Regex.Split(CanonicalizeHeadingText(text), @"[^\p{L}\p{Nd}]+")
            .Where(token => token.Length > 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        DocxBlockSummary? inferredTarget,
        SourceClauseCandidate clause,
        IEnumerable<DocxBlockSummary> availableBlocks)
    {
        if (inferredTarget is null)
            return false;

        if (string.IsNullOrWhiteSpace(targetBlockId))
            return true;

        var inferredScore = ScoreClauseAgainstBlock(clause, inferredTarget);

        if (string.Equals(targetBlockId, "__document_end__", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetBlockId, "__before_signatures__", StringComparison.OrdinalIgnoreCase))
            return inferredScore >= 0.30;

        if (explicitTarget is null)
            return true;

        if (IsIntroductoryBlock(explicitTarget, availableBlocks))
            return true;

        var explicitScore = ScoreClauseAgainstBlock(clause, explicitTarget);
        return inferredScore >= 0.12 && inferredScore >= explicitScore + 0.05;
    }

    private static DocxBlockSummary? FindContainingMajorSection(
        DocxBlockSummary targetBlock,
        IEnumerable<DocxBlockSummary> availableBlocks)
    {
        return availableBlocks
            .Where(block => block.Sequence <= targetBlock.Sequence && IsMajorSectionHeadingText(block.Heading))
            .OrderByDescending(block => block.Sequence)
            .FirstOrDefault();
    }

    private static bool IsClearlyMismatchedDomain(
        SourceClauseCandidate clause,
        DocxBlockSummary targetBlock)
    {
        var sourceLabel = CanonicalizeHeadingText(clause.Label);
        if (!sourceLabel.Contains("ADUANA", StringComparison.Ordinal))
            return false;

        var sourceTopic = CanonicalizeHeadingText($"{clause.Label} {clause.Text}");
        var targetTopic = CanonicalizeHeadingText($"{targetBlock.Heading} {targetBlock.Excerpt}");

        if (Regex.IsMatch(targetTopic, @"\b(ADUANA|ADUANERO|ARANCEL|IMPORTACION|EXPORTACION)\b"))
            return false;

        if (sourceTopic.Contains("EXCLUSION", StringComparison.Ordinal) &&
            Regex.IsMatch(targetTopic, @"\b(EXCLUSIONES?|DANOS|INCUMPLIMIENTO|RESPONSABILIDAD)\b"))
        {
            return false;
        }

        if (sourceTopic.Contains("RESPONSABIL", StringComparison.Ordinal) &&
            Regex.IsMatch(targetTopic, @"\b(OBLIGACIONES?|RESPONSABILIDAD(?:ES)?|SERVICIOS?|PRESTACION)\b"))
        {
            return false;
        }

        if (sourceTopic.Contains("TRAFICO ILICITO", StringComparison.Ordinal) &&
            Regex.IsMatch(targetTopic, @"\b(SEGURIDAD|CUMPLIMIENTO|PREVENCION|ILICITO)\b"))
        {
            return false;
        }

        return true;
    }

    private static DocxBlockSummary? InferBestTargetBlock(
        SourceClauseCandidate clause,
        IEnumerable<DocxBlockSummary> blocks)
    {
        var orderedBlocks = blocks
            .OrderBy(block => block.Sequence)
            .ToList();
        var firstSubstantiveSection = orderedBlocks
            .FirstOrDefault(block => IsMajorSectionHeadingText(block.Heading));
        var sourceIsStructuredClause = LooksLikeExplicitStructuredClause(clause.Label) ||
                                       LooksLikeExplicitStructuredClause(clause.Text);

        DocxBlockSummary? bestBlock = null;
        var bestScore = 0d;
        var secondBestScore = 0d;

        foreach (var block in orderedBlocks)
        {
            if (block.IsSignatureBlock ||
                firstSubstantiveSection is not null &&
                block.Sequence < firstSubstantiveSection.Sequence ||
                !sourceIsStructuredClause && !IsMajorSectionHeadingText(block.Heading))
                continue;

            var score = ScoreClauseAgainstBlock(clause, block);
            if (score > bestScore)
            {
                secondBestScore = bestScore;
                bestScore = score;
                bestBlock = block;
            }
            else if (score > secondBestScore)
            {
                secondBestScore = score;
            }
        }

        var hasConfidentMatch = bestScore >= 0.12 &&
                                (sourceIsStructuredClause || bestScore - secondBestScore >= 0.10);

        return hasConfidentMatch ? bestBlock : null;
    }

    private static double ScoreClauseAgainstBlock(SourceClauseCandidate clause, DocxBlockSummary block)
    {
        var clauseTopic = NormalizeComparisonText($"{clause.Label}\n{clause.Text}");
        var blockTopic = NormalizeComparisonText($"{block.Heading}\n{block.Excerpt}");

        if (string.IsNullOrWhiteSpace(clauseTopic) || string.IsNullOrWhiteSpace(blockTopic))
            return 0;

        var clauseLabelTokens = TokenizeSemanticComparisonText(clause.Label);
        var blockHeadingTokens = TokenizeSemanticComparisonText(block.Heading);
        var clauseTopicTokens = TokenizeSemanticComparisonText(clauseTopic);
        var blockTopicTokens = TokenizeSemanticComparisonText(blockTopic);

        var labelScore = ComputeTokenOverlap(clauseLabelTokens, blockHeadingTokens);
        var bodyScore = ComputeTokenOverlap(clauseTopicTokens, blockTopicTokens);

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

    private static double ComputeTokenOverlap(
        IReadOnlySet<string> sourceTokens,
        IReadOnlySet<string> targetTokens)
    {
        if (sourceTokens.Count == 0 || targetTokens.Count == 0)
            return 0;

        var shared = sourceTokens.Count(token => targetTokens.Contains(token));
        return (double)shared / sourceTokens.Count;
    }

    private static HashSet<string> TokenizeSemanticComparisonText(string? text)
    {
        var canonical = CanonicalizeHeadingText(text);
        return Regex.Split(canonical, @"[^\p{L}\p{Nd}]+")
            .Where(token => token.Length > 3 && !GenericLegalTopicTokens.Contains(token.ToLowerInvariant()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsIntroductoryBlock(
        DocxBlockSummary block,
        IEnumerable<DocxBlockSummary> availableBlocks)
    {
        var firstSubstantiveSection = availableBlocks
            .OrderBy(candidate => candidate.Sequence)
            .FirstOrDefault(candidate => IsMajorSectionHeadingText(candidate.Heading));

        return firstSubstantiveSection is not null &&
               block.Sequence < firstSubstantiveSection.Sequence;
    }

    private static string NormalizeOperationStructure(string? rawStructure)
    {
        var normalized = CanonicalizeHeadingText(rawStructure);
        return normalized switch
        {
            "ARTICLE" or "ARTICULO" => "article",
            "CLAUSE" or "CLAUSULA" => "clause",
            "SECTION" or "SECCION" => "section",
            "PARAGRAPH" or "PARRAFO" => "paragraph",
            "SUBCLAUSE" or "SUBCLAUSULA" or "INCISO" or "NUMERAL" or "LITERAL" => "subclause",
            "LIST" or "LISTA" or "ENUMERATION" or "ENUMERACION" => "list",
            "PROSE" or "PROSA" or "INLINE" or "TEXTO CORRIDO" => "prose",
            _ => "auto"
        };
    }

    private static string NormalizeOperationAction(string? rawAction)
    {
        var normalized = CanonicalizeHeadingText(rawAction);
        return normalized switch
        {
            "INSERT" or "INSERTAR" or "ADD" or "AGREGAR" => "insert",
            "SKIP" or "OMIT" or "OMITIR" or "NO INSERTAR" => "skip",
            _ => string.Empty
        };
    }

    private static bool IsParagraphStructure(string structure)
    {
        return structure is "paragraph" or "subclause";
    }

    private static bool IsStandaloneStructure(string structure)
    {
        return structure is "article" or "clause" or "section";
    }

    private static bool IsSafeStructuralHeading(
        string? requestedHeading,
        SourceClauseCandidate clause)
    {
        var normalizedHeading = NormalizeOptionalText(requestedHeading);
        if (string.IsNullOrWhiteSpace(normalizedHeading))
            return false;

        var canonicalHeading = CanonicalizeHeadingText(normalizedHeading);
        if (!Regex.IsMatch(
                canonicalHeading,
                @"^(ARTICULO|CLAUSULA|SECCION|CAPITULO|PARRAFO|NUMERAL|INCISO|LITERAL)\b",
                RegexOptions.Compiled))
        {
            return false;
        }

        var labelTokens = TokenizeSemanticComparisonText(clause.Label);
        var headingTokens = TokenizeSemanticComparisonText(normalizedHeading);
        return labelTokens.Count == 0 || ComputeTokenOverlap(labelTokens, headingTokens) >= 0.25;
    }

    private static string DeterminePreferredPlacement(
        SourceClauseCandidate clause,
        DocxBlockSummary? targetBlock,
        string? requestedPlacement = null,
        string structure = "auto")
    {
        if (targetBlock is null)
        {
            return string.IsNullOrWhiteSpace(requestedPlacement)
                ? "append_end"
                : requestedPlacement;
        }

        if (IsParagraphStructure(structure) && IsMajorSectionHeadingText(targetBlock.Heading))
            return "before";

        if (IsStandaloneStructure(structure) ||
            string.Equals(structure, "list", StringComparison.OrdinalIgnoreCase))
        {
            return "after";
        }

        if (!string.IsNullOrWhiteSpace(requestedPlacement))
            return requestedPlacement;

        if (string.Equals(structure, "prose", StringComparison.OrdinalIgnoreCase))
            return "after";

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
                    .Select(ExtractTableCellText)
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

    private static string? ExtractTableCellText(TableCell cell)
    {
        var paragraphTexts = cell.Descendants<Paragraph>()
            .Select(paragraph => paragraph.InnerText?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToList();

        if (paragraphTexts.Count == 0)
            return NormalizeOptionalText(cell.InnerText);

        // A blank line is intentional: downstream clause parsing uses it to
        // preserve true Word paragraph boundaries instead of flattening the
        // entire table cell into one legal paragraph.
        return NormalizeOptionalText(string.Join("\n\n", paragraphTexts));
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
        SourceClauseCandidate clause,
        DocxBlockSummary? targetBlock,
        IEnumerable<DocxBlockSummary> availableBlocks,
        string? placement,
        string structure,
        string? requestedHeading)
    {
        var label = NormalizeOptionalText(clause.Label) ?? "Cláusula adicional";
        if (string.Equals(structure, "prose", StringComparison.OrdinalIgnoreCase))
            return null;

        var shouldUseParagraphHeading = IsParagraphStructure(structure)
            ? targetBlock is not null && IsMajorSectionHeadingText(targetBlock.Heading)
            : string.Equals(structure, "auto", StringComparison.OrdinalIgnoreCase) &&
              ShouldUseParagraphHeadingForClause(clause, targetBlock) &&
              placement is "before" or "inside_start" or "inside-start" or "prepend";

        if (!IsStandaloneStructure(structure) &&
            ShouldSuppressStandaloneHeading(clause, targetBlock, shouldUseParagraphHeading))
            return null;

        if (!shouldUseParagraphHeading)
        {
            return IsSafeStructuralHeading(requestedHeading, clause)
                ? NormalizeOptionalText(requestedHeading)
                : label;
        }


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

        var paragraphs = new List<string>();
        var blocks = normalizedBody
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var proseLines = new List<string>();
            var bulletWasStarted = false;

            foreach (var rawLine in block.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (ListItemMarkerPattern.IsMatch(line))
                {
                    if (proseLines.Count > 0)
                    {
                        paragraphs.Add(string.Join(" ", proseLines));
                        proseLines.Clear();
                    }

                    var itemText = ListItemMarkerPattern.Replace(line, string.Empty, 1).Trim();
                    if (itemText.Length > 0)
                        paragraphs.Add($"• {itemText}");

                    bulletWasStarted = true;
                    continue;
                }

                if (bulletWasStarted && paragraphs.Count > 0)
                {
                    paragraphs[^1] = $"{paragraphs[^1]} {line}".Trim();
                    continue;
                }

                proseLines.Add(line);
            }

            if (proseLines.Count > 0)
                paragraphs.Add(string.Join(" ", proseLines));
        }

        return paragraphs
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
            sb.AppendLine("Parrafos fuente indexados:");

            var sourceParagraphs = SplitClauseBodyIntoParagraphs(
                ExtractClauseBody(clause.Text, clause.Label));
            for (var i = 0; i < sourceParagraphs.Count; i++)
                sb.AppendLine($"[{i + 1}] {sourceParagraphs[i]}");

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

    private static int ResolveNonNegativeInt(string? rawValue, int fallback)
    {
        return int.TryParse(rawValue, out var parsed) && parsed >= 0
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
            string error,
            string? rawText = null) => new(false, provider, null, rawText, error);
    }

    private sealed record ProviderExecutionResult(bool Success, string? Text, string? Error)
    {
        public static ProviderExecutionResult FromSuccess(string text) => new(true, text, null);
        public static ProviderExecutionResult FromError(string error) => new(false, null, error);
    }
}
