using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace LlamaIntegrationAPI.Services.Implementations;

internal static class DocxOriginalFormatMerger
{
    private const string PlaceholderAnchorKey = "__placeholder_clause_anchor__";

    private static readonly Regex HeadingPattern = new(
        @"^(ART[ÍI]CULO|CL[ÁA]USULA|SECCI[ÓO]N|CAP[ÍI]TULO|PAR[ÁA]GRAFO)\b|^(?:\d+\.|[IVXLC]+\.)\s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MajorSectionHeadingPattern = new(
        @"^(ART[ÍI]CULO|CL[ÁA]USULA|SECCI[ÓO]N|CAP[ÍI]TULO)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SubClauseHeadingPattern = new(
        @"^(PAR[ÁA]GRAFO|NUMERAL|INCISO|LITERAL)\b|^(?:\d+\.|[IVXLC]+\.)\s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SignaturePattern = new(
        @"EN FE DE LO CUAL|FIRMAS|FIRMAN|POR EL CLIENTE|POR EL PROVEEDOR|ACEPTACI[ÓO]N|SIGNATURE",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlaceholderPattern = new(
        @"secuencia[_\s-]*clausulas|^«.*»$|^Â«.*Â»$|^<<.*>>$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ListItemMarkerPattern = new(
        @"^\s*[\u2022\u25CF\u25AA\u25E6\u2023\u2043]\s*",
        RegexOptions.Compiled);

    private static readonly Regex ParagraphHeadingRegex = new(
        @"^(P[ÁA]RRAFO)\s+([IVXLCDM]+)(\s*:?\s*)(.*)$",
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
                block.IsSignatureBlock,
                block.MaxParagraphOrdinalValue,
                block.ParagraphOrdinalValues))
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
            var placeholderParagraph = FindPlaceholderParagraph(body);
            var numberingPart = document.MainDocumentPart?.NumberingDefinitionsPart;

            foreach (var operation in operations)
                ApplyOperation(
                    body,
                    blocks,
                    operation,
                    insertionAnchors,
                    placeholderParagraph,
                    numberingPart);

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
        var paragraphs = body.Descendants<Paragraph>()
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

        var fullText = string.Join(
            "\n",
            paragraphs.Select(GetParagraphText).Where(text => !string.IsNullOrWhiteSpace(text)));
        var paragraphOrdinalValues = ExtractParagraphOrdinalValues(fullText);
        var maxParagraphOrdinalValue = paragraphOrdinalValues.Count == 0 ? 0 : paragraphOrdinalValues.Max();
        var excerpt = fullText;

        if (excerpt.Length > 500)
            excerpt = excerpt[..500] + "...";

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
            paragraphs.Any(paragraph => IsSignatureText(GetParagraphText(paragraph))),
            maxParagraphOrdinalValue,
            paragraphOrdinalValues,
            paragraphs.ToList());
    }

    private static List<int> ExtractParagraphOrdinalValues(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var normalized = CanonicalizeHeadingText(text);
        var values = new List<int>();

        foreach (Match match in Regex.Matches(
                     normalized,
                     @"P[ÃA]RRAFO\s+([IVXLCDM]+)\b",
                     RegexOptions.IgnoreCase | RegexOptions.Compiled))
        {
            var roman = match.Groups[1].Value.ToUpperInvariant();
            var value = RomanToInt(roman);
            if (value > 0)
                values.Add(value);
        }

        return values
            .Distinct()
            .OrderBy(value => value)
            .ToList();
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

    private static bool IsHeadingParagraph(Paragraph paragraph)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var text = GetParagraphText(paragraph);

        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!string.IsNullOrWhiteSpace(styleId) &&
            styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
            return true;

        return LooksLikeStructuredHeading(text);
    }

    private static void ApplyOperation(
        Body body,
        IReadOnlyList<DocxBlock> blocks,
        DocxMergeOperation operation,
        IDictionary<string, OpenXmlElement> insertionAnchors,
        Paragraph? placeholderParagraph,
        NumberingDefinitionsPart? numberingPart)
    {
        var normalizedContent = NormalizeOperationContent(operation);
        if (normalizedContent.Paragraphs.Count == 0 && string.IsNullOrWhiteSpace(normalizedContent.Heading))
            return;

        var targetBlock = ResolveTargetBlock(blocks, operation.TargetBlockId);
        var placeholderTemplate = targetBlock is null &&
                                  placeholderParagraph is not null &&
                                  placeholderParagraph.Parent is not null
            ? placeholderParagraph
            : null;
        var bodyTemplate = ResolveBodyTemplate(targetBlock, placeholderTemplate, blocks, body, normalizedContent);
        var listTemplate = FindListParagraphTemplate(targetBlock, blocks, body);
        var containsListItems = normalizedContent.Paragraphs
            .Any(paragraph => ListItemMarkerPattern.IsMatch(NormalizeOperationText(paragraph)));
        var restartedListNumberingId = containsListItems
            ? CreateRestartedListNumberingId(numberingPart, listTemplate)
            : null;
        var targetHeadingTemplate = targetBlock?.HeadingParagraph
            ?? blocks
                .Where(block => IsMajorSectionHeadingText(block.HeadingText) && !block.IsSignatureBlock)
                .OrderByDescending(block => block.Sequence)
                .Select(block => block.HeadingParagraph)
                .FirstOrDefault(paragraph => paragraph is not null)
            ?? placeholderTemplate
            ?? body.Descendants<Paragraph>().FirstOrDefault();
        var subClauseHeadingTemplate = FindSubClauseHeadingTemplate(targetBlock);
        var headingTemplate = IsStandaloneStructure(operation.Structure) ||
                              string.Equals(operation.Structure, "list", StringComparison.OrdinalIgnoreCase)
            ? targetHeadingTemplate
            : ShouldUseBodyTemplateForHeading(targetBlock, normalizedContent.Heading)
            ? subClauseHeadingTemplate ?? bodyTemplate
            : bodyTemplate;

        var insertedParagraphs = new List<Paragraph>();
        var reuseBodyFormattingForHeading = !IsStandaloneStructure(operation.Structure) &&
                                            ShouldReuseBodyFormattingForInsertedHeading(targetBlock, normalizedContent);

        if (!string.IsNullOrWhiteSpace(normalizedContent.Heading))
            insertedParagraphs.Add(CreateParagraphFromTemplate(
                reuseBodyFormattingForHeading ? bodyTemplate : headingTemplate,
                normalizedContent.Heading,
                preserveDirectFormatting: !reuseBodyFormattingForHeading,
                suppressUnderline: IsSubClauseHeadingText(normalizedContent.Heading)));

        foreach (var paragraphText in normalizedContent.Paragraphs)
        {
            var isListItem = TryStripListItemMarker(paragraphText, out var listItemText);
            var paragraphTemplate = isListItem && listTemplate is not null
                ? listTemplate
                : bodyTemplate;
            var text = isListItem && listTemplate is not null
                ? listItemText
                : paragraphText;

            insertedParagraphs.Add(CreateParagraphFromTemplate(
                paragraphTemplate,
                text,
                preserveDirectFormatting: false,
                numberingId: isListItem && listTemplate is not null
                    ? restartedListNumberingId
                    : null));
        }

        if (insertedParagraphs.Count > 0)
        {
            EnsureMinimumParagraphSpacing(insertedParagraphs[0], beforeTwips: 120, afterTwips: 60);
            EnsureMinimumParagraphSpacing(insertedParagraphs[^1], afterTwips: 120);
        }

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

        if (placement is "append_end" or "append-end")
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

        if (targetBlock is null)
            return;

        if (TryInsertAtOrdinalPosition(body, targetBlock, normalizedContent.Heading, insertedParagraphs, insertionAnchors))
            return;

        if (ShouldInsertInsideTargetAtStart(targetBlock, placement, normalizedContent.Heading))
        {
            var targetKey = $"{targetBlock.BlockId}::inside_start";
            var anchor = insertionAnchors.TryGetValue(targetKey, out var knownAnchor)
                ? knownAnchor
                : targetBlock.HeadingParagraph ?? targetBlock.StartParagraph;

            InsertAfter(anchor, insertedParagraphs);
            insertionAnchors[targetKey] = insertedParagraphs.Last();
            return;
        }

        if (placement == "before")
        {
            var beforeKey = $"{targetBlock.BlockId}::before";
            var anchor = insertionAnchors.TryGetValue(beforeKey, out var knownBeforeAnchor)
                ? knownBeforeAnchor
                : targetBlock.StartParagraph;

            InsertBefore(anchor, insertedParagraphs);
            insertionAnchors[beforeKey] = insertedParagraphs.First();
            return;
        }

        var targetKeyAfter = targetBlock.BlockId;
        var afterAnchor = insertionAnchors.TryGetValue(targetKeyAfter, out var knownAfterAnchor)
            ? knownAfterAnchor
            : targetBlock.EndParagraph;

        InsertAfter(afterAnchor, insertedParagraphs);
        insertionAnchors[targetKeyAfter] = insertedParagraphs.Last();
    }

    private static bool TryInsertAtOrdinalPosition(
        Body body,
        DocxBlock targetBlock,
        string? heading,
        IReadOnlyList<Paragraph> insertedParagraphs,
        IDictionary<string, OpenXmlElement> insertionAnchors)
    {
        if (insertedParagraphs.Count == 0 ||
            string.IsNullOrWhiteSpace(heading) ||
            !IsMajorSectionHeadingText(targetBlock.HeadingText) ||
            !TryParseParagraphOrdinalValue(heading, out var insertedOrdinal))
        {
            return false;
        }

        var sectionHeading = targetBlock.HeadingParagraph;
        if (sectionHeading is null)
            return false;

        var documentParagraphs = body.Descendants<Paragraph>().ToList();
        var sectionStartIndex = documentParagraphs.FindIndex(paragraph => ReferenceEquals(paragraph, sectionHeading));
        if (sectionStartIndex < 0)
            return false;

        OpenXmlElement lastSectionElement = sectionHeading;

        for (var i = sectionStartIndex + 1; i < documentParagraphs.Count; i++)
        {
            var paragraph = documentParagraphs[i];
            var paragraphText = GetParagraphText(paragraph);

            if (IsMajorSectionHeadingText(paragraphText))
            {
                break;
            }

            lastSectionElement = paragraph;

            if (!TryParseParagraphOrdinalValue(paragraphText, out var existingOrdinal))
                continue;

            if (existingOrdinal <= insertedOrdinal)
                continue;

            var insertionKey = $"{targetBlock.BlockId}::ordinal_before_{existingOrdinal}";
            if (insertionAnchors.TryGetValue(insertionKey, out var knownAnchor))
            {
                InsertAfter(knownAnchor, insertedParagraphs);
            }
            else
            {
                InsertBefore(paragraph, insertedParagraphs);
            }

            insertionAnchors[insertionKey] = insertedParagraphs.Last();
            RenumberParagraphOrdinalsInSection(body, sectionHeading);
            return true;
        }

        var appendKey = $"{targetBlock.BlockId}::ordinal_after";
        var appendAnchor = insertionAnchors.TryGetValue(appendKey, out var knownAppendAnchor)
            ? knownAppendAnchor
            : lastSectionElement;

        InsertAfter(appendAnchor, insertedParagraphs);
        insertionAnchors[appendKey] = insertedParagraphs.Last();
        RenumberParagraphOrdinalsInSection(body, sectionHeading);
        return true;
    }

    private static void RenumberParagraphOrdinalsInSection(Body body, Paragraph sectionHeading)
    {
        var documentParagraphs = body.Descendants<Paragraph>().ToList();
        var sectionStartIndex = documentParagraphs.FindIndex(paragraph => ReferenceEquals(paragraph, sectionHeading));
        if (sectionStartIndex < 0)
            return;

        var expectedOrdinal = 1;

        for (var i = sectionStartIndex + 1; i < documentParagraphs.Count; i++)
        {
            var paragraph = documentParagraphs[i];
            var paragraphText = GetParagraphText(paragraph);

            if (IsMajorSectionHeadingText(paragraphText))
                break;

            if (!TryParseParagraphHeading(paragraphText, out var prefix, out _, out var separator, out var remainder))
                continue;

            var normalizedSeparator = string.IsNullOrWhiteSpace(separator) ? ": " : separator;
            var updatedText = string.IsNullOrWhiteSpace(remainder)
                ? $"{prefix} {IntToRoman(expectedOrdinal)}"
                : $"{prefix} {IntToRoman(expectedOrdinal)}{normalizedSeparator}{remainder}";

            ReplaceParagraphText(paragraph, updatedText);
            expectedOrdinal++;
        }
    }

    private static bool TryParseParagraphOrdinalValue(string text, out int ordinalValue)
    {
        ordinalValue = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = CanonicalizeHeadingText(text);
        var match = Regex.Match(
            normalized,
            @"PARRAFO\s+([IVXLCDM]+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        if (!match.Success)
            return false;

        ordinalValue = RomanToInt(match.Groups[1].Value.ToUpperInvariant());
        return ordinalValue > 0;
    }

    private static bool TryParseParagraphHeading(
        string text,
        out string prefix,
        out int ordinalValue,
        out string separator,
        out string remainder)
    {
        prefix = string.Empty;
        ordinalValue = 0;
        separator = string.Empty;
        remainder = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = ParagraphHeadingRegex.Match(text.Trim());
        if (!match.Success)
            return false;

        var parsedOrdinal = RomanToInt(match.Groups[2].Value.ToUpperInvariant());
        if (parsedOrdinal <= 0)
            return false;

        prefix = match.Groups[1].Value.ToUpperInvariant().Replace("PARRAFO", "PÁRRAFO");
        ordinalValue = parsedOrdinal;
        separator = match.Groups[3].Value;
        remainder = match.Groups[4].Value.Trim();
        return true;
    }

    private static bool TryInsertAtPlaceholder(
        Paragraph? placeholderParagraph,
        IReadOnlyList<Paragraph> insertedParagraphs,
        IDictionary<string, OpenXmlElement> insertionAnchors)
    {
        if (insertedParagraphs.Count == 0)
            return false;

        if (insertionAnchors.TryGetValue(PlaceholderAnchorKey, out var knownAnchor))
        {
            InsertAfter(knownAnchor, insertedParagraphs);
            insertionAnchors[PlaceholderAnchorKey] = insertedParagraphs.Last();
            return true;
        }

        if (placeholderParagraph is null || placeholderParagraph.Parent is null)
            return false;

        InsertBefore(placeholderParagraph, insertedParagraphs);
        insertionAnchors[PlaceholderAnchorKey] = insertedParagraphs.Last();
        placeholderParagraph.Remove();
        return true;
    }

    private static NormalizedOperationContent NormalizeOperationContent(DocxMergeOperation operation)
    {
        var paragraphs = ExtractOperationParagraphs(operation);
        var heading = NormalizeOperationText(operation.Heading);

        if (string.IsNullOrWhiteSpace(heading) && paragraphs.Count > 1)
        {
            if (LooksLikeStandaloneInsertedHeading(paragraphs[0]))
            {
                heading = paragraphs[0];
                paragraphs.RemoveAt(0);
            }
            else if (LooksLikeStandaloneInsertedHeading(paragraphs[^1]))
            {
                heading = paragraphs[^1];
                paragraphs.RemoveAt(paragraphs.Count - 1);
            }
        }

        if (!string.IsNullOrWhiteSpace(heading))
        {
            paragraphs = paragraphs
                .Where(paragraph => !string.Equals(
                    NormalizeOperationText(paragraph),
                    heading,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return new NormalizedOperationContent(heading, paragraphs);
    }

    private static List<string> ExtractOperationParagraphs(DocxMergeOperation operation)
    {
        if (operation.Paragraphs is { Count: > 0 })
        {
            return operation.Paragraphs
                .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph))
                .Select(NormalizeOperationText)
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(operation.Content))
            return [];

        return operation.Content
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeOperationText)
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph))
            .ToList();
    }

    private static bool ShouldInsertInsideTargetAtStart(
        DocxBlock? targetBlock,
        string placement,
        string? heading)
    {
        if (targetBlock?.HeadingParagraph is null || string.IsNullOrWhiteSpace(heading))
            return false;

        if (!string.Equals(placement, "before", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(placement, "inside_start", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(placement, "inside-start", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(placement, "prepend", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsMajorSectionHeadingText(targetBlock.HeadingText) && IsSubClauseHeadingText(heading);
    }

    private static bool ShouldReuseBodyFormattingForInsertedHeading(
        DocxBlock? targetBlock,
        NormalizedOperationContent normalizedContent)
    {
        if (targetBlock is null || string.IsNullOrWhiteSpace(normalizedContent.Heading))
            return false;

        if (IsSubClauseHeadingText(normalizedContent.Heading))
            return false;

        if (!IsMajorSectionHeadingText(targetBlock.HeadingText))
            return false;

        if (normalizedContent.Paragraphs.Count == 0)
            return true;

        return normalizedContent.Paragraphs.Count == 1 &&
               normalizedContent.Paragraphs[0].Length <= 400;
    }

    private static Paragraph? ResolveBodyTemplate(
        DocxBlock? targetBlock,
        Paragraph? placeholderTemplate,
        IReadOnlyList<DocxBlock> blocks,
        Body body,
        NormalizedOperationContent normalizedContent)
    {
        if (placeholderTemplate is not null &&
            !IsHeadingParagraph(placeholderTemplate) &&
            !IsCenteredParagraph(placeholderTemplate))
        {
            return placeholderTemplate;
        }

        var preferredFromTarget = FindPreferredBodyParagraph(targetBlock, normalizedContent);
        if (preferredFromTarget is not null)
            return preferredFromTarget;

        return blocks
                   .Where(block => IsMajorSectionHeadingText(block.HeadingText))
                   .SelectMany(block => block.Paragraphs)
                   .FirstOrDefault(paragraph =>
                       !IsHeadingParagraph(paragraph) &&
                       !IsListLikeParagraph(paragraph) &&
                       !IsCenteredParagraph(paragraph))
               ?? blocks
                   .SelectMany(block => block.Paragraphs)
                   .FirstOrDefault(paragraph =>
                       !IsHeadingParagraph(paragraph) &&
                       !IsListLikeParagraph(paragraph) &&
                       !IsCenteredParagraph(paragraph))
               ?? body.Descendants<Paragraph>().FirstOrDefault(paragraph =>
                   !IsHeadingParagraph(paragraph) && !IsCenteredParagraph(paragraph))
               ?? body.Descendants<Paragraph>().FirstOrDefault();
    }

    private static Paragraph? FindPreferredBodyParagraph(
        DocxBlock? targetBlock,
        NormalizedOperationContent normalizedContent)
    {
        if (targetBlock?.Paragraphs is null || targetBlock.Paragraphs.Count == 0)
            return null;

        var bodyParagraphs = targetBlock.Paragraphs
            .Where(paragraph =>
                paragraph != targetBlock.HeadingParagraph &&
                !IsHeadingParagraph(paragraph) &&
                !IsCenteredParagraph(paragraph))
            .ToList();

        if (bodyParagraphs.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(normalizedContent.Heading) && normalizedContent.Paragraphs.Count <= 1)
        {
            var nonListParagraph = bodyParagraphs.FirstOrDefault(paragraph => !IsListLikeParagraph(paragraph));
            if (nonListParagraph is not null)
                return nonListParagraph;
        }

        return bodyParagraphs.FirstOrDefault(paragraph => !IsListLikeParagraph(paragraph))
               ?? bodyParagraphs.FirstOrDefault();
    }

    private static bool IsCenteredParagraph(Paragraph paragraph)
    {
        var justification = paragraph.ParagraphProperties?.Justification?.Val?.Value;
        return justification == JustificationValues.Center;
    }

    private static bool IsListLikeParagraph(Paragraph paragraph)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrWhiteSpace(styleId) &&
            styleId.Contains("List", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (paragraph.ParagraphProperties?.NumberingProperties is not null)
            return true;

        return false;
    }

    private static bool IsStandaloneStructure(string? structure)
    {
        return structure?.Trim().ToLowerInvariant() is "article" or "clause" or "section";
    }

    private static Paragraph? FindListParagraphTemplate(
        DocxBlock? targetBlock,
        IReadOnlyList<DocxBlock> blocks,
        Body body)
    {
        return targetBlock?.Paragraphs.FirstOrDefault(IsListLikeParagraph)
               ?? blocks
                   .SelectMany(block => block.Paragraphs)
                   .FirstOrDefault(IsListLikeParagraph)
               ?? body.Descendants<Paragraph>().FirstOrDefault(IsListLikeParagraph);
    }

    private static bool TryStripListItemMarker(string text, out string itemText)
    {
        var normalized = NormalizeOperationText(text);
        var marker = ListItemMarkerPattern.Match(normalized);
        if (!marker.Success)
        {
            itemText = normalized;
            return false;
        }

        itemText = normalized[marker.Length..].Trim();
        return itemText.Length > 0;
    }

    private static int? CreateRestartedListNumberingId(
        NumberingDefinitionsPart? numberingPart,
        Paragraph? listTemplate)
    {
        var sourceNumberingId = listTemplate?
            .ParagraphProperties?
            .NumberingProperties?
            .NumberingId?
            .Val?
            .Value;

        var numbering = numberingPart?.Numbering;
        if (sourceNumberingId is null || numbering is null)
            return null;

        var sourceNumbering = numbering.Elements<NumberingInstance>()
            .FirstOrDefault(numberingInstance =>
                numberingInstance.NumberID?.Value == sourceNumberingId.Value);
        var abstractNumberingId = sourceNumbering?.AbstractNumId?.Val?.Value;
        if (abstractNumberingId is null)
            return null;

        var nextNumberingId = numbering.Elements<NumberingInstance>()
            .Select(numberingInstance => numberingInstance.NumberID?.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        var restartedNumbering = new NumberingInstance
        {
            NumberID = nextNumberingId
        };
        restartedNumbering.Append(new AbstractNumId { Val = abstractNumberingId.Value });
        var levelOverride = new LevelOverride { LevelIndex = 0 };
        levelOverride.Append(new StartOverrideNumberingValue { Val = 1 });
        restartedNumbering.Append(levelOverride);
        numbering.Append(restartedNumbering);
        numbering.Save();

        return nextNumberingId;
    }

    private static bool ShouldUseBodyTemplateForHeading(DocxBlock? targetBlock, string? heading)
    {
        return targetBlock is not null &&
               !string.IsNullOrWhiteSpace(heading) &&
               IsMajorSectionHeadingText(targetBlock.HeadingText) &&
               IsSubClauseHeadingText(heading);
    }

    private static Paragraph? FindSubClauseHeadingTemplate(DocxBlock? targetBlock)
    {
        if (targetBlock?.Paragraphs is null)
            return null;

        return targetBlock.Paragraphs
            .FirstOrDefault(paragraph =>
                paragraph != targetBlock.HeadingParagraph &&
                IsSubClauseHeadingText(GetParagraphText(paragraph)));
    }

    private static bool LooksLikeStandaloneInsertedHeading(string text)
    {
        var normalized = NormalizeOperationText(text);
        return normalized.Length <= 220 && LooksLikeStructuredHeading(normalized);
    }

    private static bool IsMajorSectionHeadingText(string text)
    {
        var normalized = CanonicalizeHeadingText(text);
        return normalized.StartsWith("ARTICULO ", StringComparison.Ordinal) ||
               normalized.StartsWith("CLAUSULA ", StringComparison.Ordinal) ||
               normalized.StartsWith("SECCION ", StringComparison.Ordinal) ||
               normalized.StartsWith("CAPITULO ", StringComparison.Ordinal);
    }

    private static bool IsSubClauseHeadingText(string text)
    {
        var normalized = CanonicalizeHeadingText(text);
        return normalized.StartsWith("PARRAFO ", StringComparison.Ordinal) ||
               normalized.StartsWith("NUMERAL ", StringComparison.Ordinal) ||
               normalized.StartsWith("INCISO ", StringComparison.Ordinal) ||
               normalized.StartsWith("LITERAL ", StringComparison.Ordinal) ||
               Regex.IsMatch(normalized, @"^(?:\d+\.|[IVXLC]+\.)\s", RegexOptions.Compiled);
    }

    private static string NormalizeOperationText(string? text)
    {
        return text?.Trim() ?? string.Empty;
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
        // Return the exact signature marker. A legal section block may span
        // several paragraphs and merely contain signatures near its end;
        // returning the block start would insert clauses far too early.
        return body.Descendants<Paragraph>()
            .FirstOrDefault(paragraph => IsSignatureText(GetParagraphText(paragraph)));
    }

    private static bool IsSignatureText(string? text)
    {
        var canonical = CanonicalizeHeadingText(text);
        if (string.IsNullOrWhiteSpace(canonical))
            return false;

        if (canonical.Contains("EN FE DE LO CUAL", StringComparison.Ordinal) ||
            canonical.Contains("HECHO Y FIRMADO", StringComparison.Ordinal) ||
            canonical.Contains("SIGNATURE", StringComparison.Ordinal) ||
            canonical.StartsWith("FIRMAS", StringComparison.Ordinal) ||
            canonical.StartsWith("ACEPTACION", StringComparison.Ordinal))
        {
            return true;
        }

        // Role labels are signature markers only when the complete paragraph
        // is short. Contract prose frequently contains "por EL CLIENTE" and
        // must never be mistaken for the signature section.
        return canonical.Length <= 80 &&
               Regex.IsMatch(
                   canonical,
                   @"^POR\s+(?:EL\s+)?(?:CLIENTE|PROVEEDOR)\s*:?$");
    }

    private static Paragraph? FindPlaceholderParagraph(Body body)
    {
        return body.Descendants<Paragraph>()
            .FirstOrDefault(paragraph => PlaceholderPattern.IsMatch(GetParagraphText(paragraph)));
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
        anchor = MoveAnchorBeforeLeadingSpacerParagraphs(anchor);

        foreach (var paragraph in paragraphs)
            anchor.InsertBeforeSelf(paragraph);
    }

    private static OpenXmlElement MoveAnchorBeforeLeadingSpacerParagraphs(OpenXmlElement anchor)
    {
        var effectiveAnchor = anchor;

        while (effectiveAnchor.PreviousSibling() is Paragraph previousParagraph &&
               string.IsNullOrWhiteSpace(GetParagraphText(previousParagraph)))
        {
            effectiveAnchor = previousParagraph;
        }

        return effectiveAnchor;
    }

    private static Paragraph CreateParagraphFromTemplate(
        Paragraph? template,
        string text,
        bool preserveDirectFormatting,
        bool suppressUnderline = false,
        int? numberingId = null)
    {
        var paragraph = new Paragraph();

        if (template?.ParagraphProperties is not null)
            paragraph.ParagraphProperties = (ParagraphProperties)template.ParagraphProperties.CloneNode(true);

        if (numberingId is not null && paragraph.ParagraphProperties?.NumberingProperties is { } numberingProperties)
            numberingProperties.NumberingId = new NumberingId { Val = numberingId.Value };

        var run = new Run();
        var templateRunProperties = template?.Elements<Run>()
            .Select(element => element.RunProperties)
            .FirstOrDefault(properties => properties is not null);

        var runProperties = CloneRunProperties(templateRunProperties, preserveDirectFormatting, suppressUnderline);
        if (runProperties is not null)
            run.RunProperties = runProperties;

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

    private static void EnsureMinimumParagraphSpacing(
        Paragraph paragraph,
        uint? beforeTwips = null,
        uint? afterTwips = null)
    {
        paragraph.ParagraphProperties ??= new ParagraphProperties();
        paragraph.ParagraphProperties.SpacingBetweenLines ??= new SpacingBetweenLines();
        var spacing = paragraph.ParagraphProperties.SpacingBetweenLines;

        if (beforeTwips is not null &&
            (!uint.TryParse(spacing.Before?.Value, out var existingBefore) || existingBefore < beforeTwips.Value))
        {
            spacing.Before = beforeTwips.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (afterTwips is not null &&
            (!uint.TryParse(spacing.After?.Value, out var existingAfter) || existingAfter < afterTwips.Value))
        {
            spacing.After = afterTwips.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static RunProperties? CloneRunProperties(
        RunProperties? source,
        bool preserveDirectFormatting,
        bool suppressUnderline = false)
    {
        if (source is null)
            return null;

        var cloned = (RunProperties)source.CloneNode(true);
        if (suppressUnderline)
            cloned.RemoveAllChildren<Underline>();

        if (preserveDirectFormatting)
            return cloned;

        cloned.RemoveAllChildren<Bold>();
        cloned.RemoveAllChildren<BoldComplexScript>();
        cloned.RemoveAllChildren<Italic>();
        cloned.RemoveAllChildren<ItalicComplexScript>();
        cloned.RemoveAllChildren<Underline>();
        cloned.RemoveAllChildren<Strike>();
        cloned.RemoveAllChildren<DoubleStrike>();
        cloned.RemoveAllChildren<Emboss>();
        cloned.RemoveAllChildren<Imprint>();
        cloned.RemoveAllChildren<Shadow>();
        cloned.RemoveAllChildren<Outline>();
        cloned.RemoveAllChildren<SmallCaps>();
        cloned.RemoveAllChildren<Caps>();
        cloned.RemoveAllChildren<Vanish>();
        cloned.RemoveAllChildren<Highlight>();
        cloned.RemoveAllChildren<Shading>();

        return cloned.HasChildren ? cloned : null;
    }

    private static void ReplaceParagraphText(Paragraph paragraph, string text)
    {
        var templateRunProperties = paragraph.Elements<Run>()
            .Select(element => element.RunProperties)
            .FirstOrDefault(properties => properties is not null);

        paragraph.RemoveAllChildren<Run>();

        var run = new Run();
        if (templateRunProperties is not null)
            run.RunProperties = (RunProperties)templateRunProperties.CloneNode(true);

        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.Append(run);
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

    private static string GetParagraphText(Paragraph paragraph)
    {
        return paragraph.InnerText?.Trim() ?? string.Empty;
    }

    private static bool LooksLikeStructuredHeading(string text)
    {
        var normalized = CanonicalizeHeadingText(text);
        return normalized.StartsWith("ARTICULO ", StringComparison.Ordinal) ||
               normalized.StartsWith("CLAUSULA ", StringComparison.Ordinal) ||
               normalized.StartsWith("SECCION ", StringComparison.Ordinal) ||
               normalized.StartsWith("CAPITULO ", StringComparison.Ordinal) ||
               normalized.StartsWith("PARRAFO ", StringComparison.Ordinal) ||
               normalized.StartsWith("NUMERAL ", StringComparison.Ordinal) ||
               normalized.StartsWith("INCISO ", StringComparison.Ordinal) ||
               normalized.StartsWith("LITERAL ", StringComparison.Ordinal) ||
               Regex.IsMatch(normalized, @"^(?:\d+\.|[IVXLC]+\.)\s", RegexOptions.Compiled);
    }

    private static string CanonicalizeHeadingText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
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
}

internal sealed record DocxBlockSummary(
    string BlockId,
    int Sequence,
    string Heading,
    string Excerpt,
    bool IsSignatureBlock,
    int MaxParagraphOrdinalValue,
    IReadOnlyList<int> ParagraphOrdinalValues);

internal sealed record DocxMergeOperation
{
    public string? TargetBlockId { get; init; }
    [JsonPropertyName("target_block_id")]
    public string? TargetBlockIdAlias { get; init; }
    public string? Placement { get; init; }
    public string? SourceClauseId { get; init; }
    [JsonPropertyName("clause_id")]
    public string? SourceClauseIdAlias { get; init; }
    public string? Action { get; init; }
    public string? Structure { get; init; }
    [JsonPropertyName("unit")]
    public string? StructureAlias { get; init; }
    [JsonPropertyName("unidad juridica")]
    public string? LegalUnitAlias { get; init; }
    public List<int> SourceParagraphIndexes { get; init; } = [];
    [JsonPropertyName("source_paragraph_indexes")]
    public List<int> SourceParagraphIndexesAlias { get; init; } = [];
    public double? Confidence { get; init; }
    public string? Heading { get; init; }
    public string? Content { get; init; }
    public List<string> Paragraphs { get; init; } = [];
    public string? Reason { get; init; }
}

internal sealed record DocxMergePlan
{
    public string? Summary { get; init; }
    public List<DocxMergeOperation> Operations { get; init; } = [];
    [JsonPropertyName("clauses")]
    public List<DocxMergeOperation> ClausesAlias { get; init; } = [];
    [JsonPropertyName("decisions")]
    public List<DocxMergeOperation> DecisionsAlias { get; init; } = [];
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; init; } = [];
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
    bool IsSignatureBlock,
    int MaxParagraphOrdinalValue,
    IReadOnlyList<int> ParagraphOrdinalValues,
    IReadOnlyList<Paragraph> Paragraphs);

internal sealed record NormalizedOperationContent(
    string? Heading,
    List<string> Paragraphs);
