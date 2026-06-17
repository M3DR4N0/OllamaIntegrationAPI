using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace LlamaIntegrationAPI.Services.Implementations;

internal static class DocxOriginalFormatMerger
{
    private static readonly Regex HeadingPattern = new(
        @"^(ART[ÍI]CULO|CL[ÁA]USULA|SECCI[ÓO]N|CAP[ÍI]TULO|PAR[ÁA]GRAFO)\b|^(?:\d+\.|[IVXLC]+\.)\s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SignaturePattern = new(
        @"EN FE DE LO CUAL|FIRMAS|FIRMAN|POR EL CLIENTE|POR EL PROVEEDOR|ACEPTACI[ÓO]N|SIGNATURE",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<DocxBlockSummary> Summarize(byte[] docxBytes)
    {
        using var stream = new MemoryStream(docxBytes);
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("El documento base no contiene cuerpo principal.");

        return BuildBlocks(body)
            .Select(block => new DocxBlockSummary(
                block.BlockId,
                block.Sequence,
                block.HeadingText,
                block.Excerpt,
                block.IsSignatureBlock))
            .ToList();
    }

    public static byte[] ApplyOperations(byte[] docxBytes, IReadOnlyList<DocxMergeOperation> operations)
    {
        using var stream = new MemoryStream();
        stream.Write(docxBytes, 0, docxBytes.Length);
        stream.Position = 0;

        using (var document = WordprocessingDocument.Open(stream, true))
        {
            var body = document.MainDocumentPart?.Document?.Body
                ?? throw new InvalidOperationException("El documento base no contiene cuerpo principal.");

            var blocks = BuildBlocks(body);
            var insertionAnchors = new Dictionary<string, OpenXmlElement>(StringComparer.OrdinalIgnoreCase);

            foreach (var operation in operations)
            {
                ApplyOperation(body, blocks, operation, insertionAnchors);
            }

            document.MainDocumentPart?.Document?.Save();
        }

        return stream.ToArray();
    }

    public static bool LooksLikeDocx(IFormFile file)
    {
        return file.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   file.ContentType,
                   "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static List<DocxBlock> BuildBlocks(Body body)
    {
        var paragraphs = body.Elements<Paragraph>()
            .Where(p => !string.IsNullOrWhiteSpace(GetParagraphText(p)))
            .ToList();

        if (paragraphs.Count == 0)
            return [];

        var blocks = new List<DocxBlock>();
        var currentParagraphs = new List<Paragraph>();
        Paragraph? currentHeading = null;
        var sequence = 1;

        foreach (var paragraph in paragraphs)
        {
            var isHeading = IsHeadingParagraph(paragraph);

            if (isHeading && currentParagraphs.Count > 0)
            {
                blocks.Add(CreateBlock(sequence++, currentHeading, currentParagraphs));
                currentParagraphs = [];
            }

            if (isHeading)
                currentHeading = paragraph;

            currentParagraphs.Add(paragraph);
        }

        if (currentParagraphs.Count > 0)
            blocks.Add(CreateBlock(sequence, currentHeading, currentParagraphs));

        return blocks;
    }

    private static DocxBlock CreateBlock(int sequence, Paragraph? headingParagraph, List<Paragraph> paragraphs)
    {
        var headingText = headingParagraph is null
            ? (sequence == 1 ? "PREAMBULO" : $"BLOQUE {sequence}")
            : GetParagraphText(headingParagraph);

        var excerpt = string.Join(
            "\n",
            paragraphs.Select(GetParagraphText).Where(text => !string.IsNullOrWhiteSpace(text)));

        if (excerpt.Length > 1200)
            excerpt = excerpt[..1200] + "...";

        var firstBodyParagraph = paragraphs.FirstOrDefault(p => p != headingParagraph) ?? paragraphs.FirstOrDefault();

        return new DocxBlock(
            $"block_{sequence:0000}",
            sequence,
            headingParagraph,
            paragraphs.First(),
            paragraphs.Last(),
            firstBodyParagraph,
            headingText,
            excerpt,
            SignaturePattern.IsMatch(headingText) || SignaturePattern.IsMatch(excerpt));
    }

    private static bool IsHeadingParagraph(Paragraph paragraph)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var text = GetParagraphText(paragraph);

        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!string.IsNullOrWhiteSpace(styleId) &&
            styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
            return true;

        return HeadingPattern.IsMatch(text);
    }

    private static void ApplyOperation(
        Body body,
        IReadOnlyList<DocxBlock> blocks,
        DocxMergeOperation operation,
        IDictionary<string, OpenXmlElement> insertionAnchors)
    {
        var paragraphs = NormalizeOperationParagraphs(operation);
        if (paragraphs.Count == 0)
            return;

        var targetBlock = ResolveTargetBlock(blocks, operation.TargetBlockId);
        var headingTemplate = targetBlock?.HeadingParagraph
            ?? blocks.FirstOrDefault(block => block.HeadingParagraph is not null)?.HeadingParagraph
            ?? body.Elements<Paragraph>().FirstOrDefault();
        var bodyTemplate = targetBlock?.FirstBodyParagraph
            ?? blocks.FirstOrDefault(block => block.FirstBodyParagraph is not null)?.FirstBodyParagraph
            ?? body.Elements<Paragraph>().FirstOrDefault();

        var insertedParagraphs = new List<Paragraph>();

        if (!string.IsNullOrWhiteSpace(operation.Heading))
            insertedParagraphs.Add(CreateParagraphFromTemplate(headingTemplate, operation.Heading));

        insertedParagraphs.AddRange(paragraphs.Select(paragraph => CreateParagraphFromTemplate(bodyTemplate, paragraph)));

        var placement = (operation.Placement ?? "after").Trim().ToLowerInvariant();

        if (placement is "before_signatures" or "before-signatures")
        {
            var signatureParagraph = FindSignatureParagraph(blocks, body);
            if (signatureParagraph is not null)
            {
                InsertBefore(signatureParagraph, insertedParagraphs);
                return;
            }
        }

        if (placement is "append_end" or "append-end" || targetBlock is null)
        {
            var appendKey = "__document_end__";
            var appendAnchor = insertionAnchors.TryGetValue(appendKey, out var knownAppendAnchor)
                ? knownAppendAnchor
                : body.Elements<OpenXmlElement>().LastOrDefault();

            if (appendAnchor is null)
                return;

            InsertAfter(appendAnchor, insertedParagraphs);
            insertionAnchors[appendKey] = insertedParagraphs.Last();
            return;
        }

        if (placement == "before")
        {
            InsertBefore(targetBlock.StartParagraph, insertedParagraphs);
            return;
        }

        var targetKey = targetBlock.BlockId;
        var anchor = insertionAnchors.TryGetValue(targetKey, out var knownAnchor)
            ? knownAnchor
            : targetBlock.EndParagraph;

        InsertAfter(anchor, insertedParagraphs);
        insertionAnchors[targetKey] = insertedParagraphs.Last();
    }

    private static List<string> NormalizeOperationParagraphs(DocxMergeOperation operation)
    {
        if (operation.Paragraphs is { Count: > 0 })
        {
            return operation.Paragraphs
                .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph))
                .Select(paragraph => paragraph.Trim())
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(operation.Content))
            return [];

        return operation.Content
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph))
            .ToList();
    }

    private static DocxBlock? ResolveTargetBlock(IReadOnlyList<DocxBlock> blocks, string? targetBlockId)
    {
        if (string.IsNullOrWhiteSpace(targetBlockId))
            return blocks.LastOrDefault(block => !block.IsSignatureBlock) ?? blocks.LastOrDefault();

        return blocks.FirstOrDefault(block =>
            string.Equals(block.BlockId, targetBlockId, StringComparison.OrdinalIgnoreCase));
    }

    private static Paragraph? FindSignatureParagraph(IReadOnlyList<DocxBlock> blocks, Body body)
    {
        var signatureBlock = blocks.FirstOrDefault(block => block.IsSignatureBlock);
        if (signatureBlock is not null)
            return signatureBlock.StartParagraph;

        return body.Elements<Paragraph>()
            .FirstOrDefault(paragraph => SignaturePattern.IsMatch(GetParagraphText(paragraph)));
    }

    private static void InsertAfter(OpenXmlElement? anchor, IReadOnlyList<Paragraph> paragraphs)
    {
        if (anchor is null)
            return;

        OpenXmlElement currentAnchor = anchor;
        foreach (var paragraph in paragraphs)
        {
            currentAnchor.InsertAfterSelf(paragraph);
            currentAnchor = paragraph;
        }
    }

    private static void InsertBefore(OpenXmlElement anchor, IReadOnlyList<Paragraph> paragraphs)
    {
        for (var i = paragraphs.Count - 1; i >= 0; i--)
            anchor.InsertBeforeSelf(paragraphs[i]);
    }

    private static Paragraph CreateParagraphFromTemplate(Paragraph? template, string text)
    {
        var paragraph = new Paragraph();

        if (template?.ParagraphProperties is not null)
            paragraph.ParagraphProperties = (ParagraphProperties)template.ParagraphProperties.CloneNode(true);

        var run = new Run();
        var runProperties = template?.Elements<Run>()
            .Select(element => element.RunProperties)
            .FirstOrDefault(properties => properties is not null);

        if (runProperties is not null)
            run.RunProperties = (RunProperties)runProperties.CloneNode(true);

        var normalizedLines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < normalizedLines.Length; i++)
        {
            if (i > 0)
                run.Append(new Break());

            run.Append(new Text(normalizedLines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }

        paragraph.Append(run);
        return paragraph;
    }

    private static string GetParagraphText(Paragraph paragraph)
    {
        return paragraph.InnerText?.Trim() ?? string.Empty;
    }
}

internal sealed record DocxBlockSummary(
    string BlockId,
    int Sequence,
    string Heading,
    string Excerpt,
    bool IsSignatureBlock);

internal sealed record DocxMergeOperation
{
    public string? TargetBlockId { get; init; }
    public string? Placement { get; init; }
    public string? Heading { get; init; }
    public string? Content { get; init; }
    public List<string> Paragraphs { get; init; } = [];
    public string? Reason { get; init; }
}

internal sealed record DocxMergePlan
{
    public string? Summary { get; init; }
    public List<DocxMergeOperation> Operations { get; init; } = [];
}

internal sealed record DocxBlock(
    string BlockId,
    int Sequence,
    Paragraph? HeadingParagraph,
    Paragraph StartParagraph,
    Paragraph EndParagraph,
    Paragraph? FirstBodyParagraph,
    string HeadingText,
    string Excerpt,
    bool IsSignatureBlock);
