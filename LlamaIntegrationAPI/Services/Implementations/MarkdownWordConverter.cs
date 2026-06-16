using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LlamaIntegrationAPI.Services.Implementations;

/// <summary>
/// Converts Markdown into DOCX using a fully free pipeline based on
/// Markdig and the Open XML SDK.
/// </summary>
public static class MarkdownWordConverter
{
    private static readonly Markdig.MarkdownPipeline Pipeline = new Markdig.MarkdownPipelineBuilder()
        .Build();

    public static byte[] ConvertToDocx(string markdown, string? title = null)
    {
        using var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(
                   stream,
                   WordprocessingDocumentType.Document,
                   true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            if (!string.IsNullOrWhiteSpace(title))
            {
                body.Append(CreateParagraph(title, styleId: "Title"));
            }

            var markdownDocument = Markdig.Markdown.Parse(markdown?.Trim() ?? string.Empty, Pipeline);
            AppendBlocks(body, markdownDocument);
            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    private static void AppendBlocks(Body body, IEnumerable<Block> blocks, int listDepth = 0)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    body.Append(CreateParagraph(
                        ExtractInlineText(heading.Inline),
                        styleId: $"Heading{Math.Clamp(heading.Level, 1, 6)}"));
                    break;

                case ParagraphBlock paragraph:
                    body.Append(CreateParagraphFromInline(paragraph.Inline, listDepth));
                    break;

                case ListBlock list:
                    AppendList(body, list, listDepth);
                    break;

                case QuoteBlock quote:
                    AppendQuote(body, quote, listDepth);
                    break;

                case ThematicBreakBlock:
                    body.Append(CreateParagraph("----------------"));
                    break;

                case FencedCodeBlock fencedCode:
                    AppendCodeBlock(body, fencedCode);
                    break;

                case CodeBlock codeBlock:
                    AppendCodeBlock(body, codeBlock);
                    break;
            }
        }
    }

    private static void AppendList(Body body, ListBlock list, int listDepth)
    {
        var index = 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var firstParagraphWritten = false;
            foreach (var child in item)
            {
                if (!firstParagraphWritten && child is ParagraphBlock paragraph)
                {
                    var prefix = list.IsOrdered
                        ? $"{index}. "
                        : "- ";
                    body.Append(CreateParagraphFromInline(paragraph.Inline, listDepth, prefix));
                    firstParagraphWritten = true;
                }
                else if (child is ParagraphBlock nestedParagraph)
                {
                    body.Append(CreateParagraphFromInline(nestedParagraph.Inline, listDepth + 1));
                }
                else if (child is ListBlock nestedList)
                {
                    AppendList(body, nestedList, listDepth + 1);
                }
                else if (child is QuoteBlock quote)
                {
                    AppendQuote(body, quote, listDepth + 1);
                }
            }

            if (list.IsOrdered)
                index++;
        }
    }

    private static void AppendQuote(Body body, QuoteBlock quote, int listDepth)
    {
        foreach (var child in quote)
        {
            if (child is ParagraphBlock paragraph)
            {
                body.Append(CreateParagraphFromInline(paragraph.Inline, listDepth, "> "));
            }
            else if (child is ListBlock nestedList)
            {
                AppendList(body, nestedList, listDepth + 1);
            }
        }
    }

    private static void AppendCodeBlock(Body body, LeafBlock block)
    {
        var lines = block.Lines.ToString() ?? string.Empty;
        body.Append(CreateParagraph(lines, styleId: null, monospace: true));
    }

    private static Paragraph CreateParagraphFromInline(
        ContainerInline? inline,
        int listDepth,
        string? prefix = null)
    {
        var paragraph = new Paragraph();
        paragraph.Append(CreateParagraphProperties(styleId: null, listDepth));

        if (!string.IsNullOrWhiteSpace(prefix))
        {
            paragraph.Append(CreateRun(prefix, bold: false, italic: false, monospace: false));
        }

        foreach (var run in BuildRuns(inline))
            paragraph.Append(run);

        return paragraph;
    }

    private static Paragraph CreateParagraph(
        string text,
        string? styleId = null,
        bool monospace = false,
        int listDepth = 0)
    {
        var paragraph = new Paragraph();
        paragraph.Append(CreateParagraphProperties(styleId, listDepth));
        paragraph.Append(CreateRun(text, bold: false, italic: false, monospace: monospace));
        return paragraph;
    }

    private static ParagraphProperties CreateParagraphProperties(string? styleId, int listDepth)
    {
        var properties = new ParagraphProperties();

        if (!string.IsNullOrWhiteSpace(styleId))
            properties.Append(new ParagraphStyleId { Val = styleId });

        if (listDepth > 0)
        {
            properties.Append(new Indentation
            {
                Left = (listDepth * 720).ToString()
            });
        }

        return properties;
    }

    private static IEnumerable<Run> BuildRuns(ContainerInline? inline, bool bold = false, bool italic = false)
    {
        if (inline is null)
            yield break;

        var current = inline.FirstChild;
        while (current is not null)
        {
            switch (current)
            {
                case LiteralInline literal:
                    yield return CreateRun(
                        literal.Content.ToString(),
                        bold,
                        italic,
                        monospace: false);
                    break;

                case LineBreakInline:
                    yield return new Run(new Break());
                    break;

                case CodeInline codeInline:
                    yield return CreateRun(
                        codeInline.Content,
                        bold,
                        italic,
                        monospace: true);
                    break;

                case EmphasisInline emphasis:
                    foreach (var run in BuildRuns(
                                 emphasis,
                                 bold || emphasis.DelimiterChar == '*' && emphasis.DelimiterCount >= 2,
                                 italic || emphasis.DelimiterCount == 1))
                    {
                        yield return run;
                    }
                    break;

                case LinkInline link:
                    foreach (var run in BuildRuns(link, bold, italic))
                        yield return run;

                    if (!string.IsNullOrWhiteSpace(link.Url))
                        yield return CreateRun($" ({link.Url})", false, false, false);
                    break;
            }

            current = current.NextSibling;
        }
    }

    private static string ExtractInlineText(ContainerInline? inline)
    {
        return string.Concat(BuildRuns(inline).Select(run => run.InnerText));
    }

    private static Run CreateRun(string text, bool bold, bool italic, bool monospace)
    {
        var run = new Run();
        var properties = new RunProperties();

        if (bold)
            properties.Append(new Bold());

        if (italic)
            properties.Append(new Italic());

        if (monospace)
        {
            properties.Append(new RunFonts
            {
                Ascii = "Consolas",
                HighAnsi = "Consolas"
            });
        }

        if (properties.ChildElements.Count > 0)
            run.Append(properties);

        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }
}
