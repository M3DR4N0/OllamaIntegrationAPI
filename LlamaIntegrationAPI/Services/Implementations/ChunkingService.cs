using LlamaIntegrationAPI.Models.Rag;
using LlamaIntegrationAPI.Services.Interfaces;
using SharpToken;
using System.Text.RegularExpressions;

namespace LlamaIntegrationAPI.Services.Implementations;

/// <summary>
/// Splits document text into chunks for embedding and retrieval.
/// <list type="number">
///   <item>Tries semantic splitting first (articles, sections, clauses, chapters).</item>
///   <item>Sub-chunks oversized segments with token-based sliding window.</item>
///   <item>Falls back entirely to token-based chunking when no structure is detected.</item>
/// </list>
/// </summary>
public sealed class ChunkingService(ILogger<ChunkingService> logger) : IChunkingService
{
    // ── Configuration ────────────────────────────────────────────────

    private const int MaxTokensPerChunk = 400;
    private const int OverlapTokens = 50;
    private const int MinSemanticHeadings = 2;

    // ── Tokeniser (cached, thread-safe) ──────────────────────────────

    private static readonly GptEncoding Encoding = GptEncoding.GetEncoding("cl100k_base");

    // ── Semantic heading patterns (Spanish + English, case-insensitive) ──

    private static readonly Regex HeadingPattern = new(
        @"^[ \t]*(?:" +
            @"Art(?:ículo|icle|\.)\s*\d+" +                         // Artículo 1 / Article 1 / Art. 1
            @"|ARTICLE\s+[\dIVXLCDM]+" +                            // ARTICLE IV
            @"|Secci[oó]n\s+[\dIVXLCDM]+" +                         // Sección 1
            @"|Section\s+[\dIVXLCDM]+" +                             // Section 1
            @"|Cap[ií]tulo\s+[\dIVXLCDM]+" +                         // Capítulo I
            @"|Chapter\s+[\dIVXLCDM]+" +                             // Chapter I
            @"|T[ií]tulo\s+[\dIVXLCDM]+" +                           // Título II
            @"|Title\s+[\dIVXLCDM]+" +                               // Title II
            @"|Cl[aá]usula\s+\d+" +                                  // Cláusula 3
            @"|Clause\s+\d+" +                                       // Clause 3
            @"|Disposici[oó]n\s+(transitoria\s+)?\d+" +              // Disposición transitoria 1
            @"|Anexo\s+[\dIVXLCDMA-Z]+" +                            // Anexo A / Anexo I
            @"|Annex\s+[\dIVXLCDMA-Z]+" +                            // Annex A
        @")",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Public API ───────────────────────────────────────────────────

    public IReadOnlyList<DocumentChunk> Chunk(string text, ChunkMetadata baseMetadata)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // 1️⃣ Attempt semantic splitting
        var semanticChunks = TrySemanticSplit(text, baseMetadata);
        if (semanticChunks.Count > 0)
        {
            logger.LogInformation(
                "Semantic chunking produced {Count} chunks for '{Doc}'.",
                semanticChunks.Count, baseMetadata.DocumentName);
            return semanticChunks;
        }

        // 2️⃣ Fallback: token-based
        var tokenChunks = TokenSplit(text, baseMetadata);
        logger.LogInformation(
            "Token-based fallback produced {Count} chunks for '{Doc}'.",
            tokenChunks.Count, baseMetadata.DocumentName);
        return tokenChunks;
    }

    // ── Semantic splitting ───────────────────────────────────────────

    private List<DocumentChunk> TrySemanticSplit(string text, ChunkMetadata baseMetadata)
    {
        var matches = HeadingPattern.Matches(text);
        if (matches.Count < MinSemanticHeadings)
            return [];

        var result = new List<DocumentChunk>();

        // Handle preamble (text before the first heading)
        if (matches[0].Index > 0)
        {
            var preamble = text[..matches[0].Index].Trim();
            if (!string.IsNullOrWhiteSpace(preamble))
            {
                AddSegment(result, preamble, baseMetadata, section: "Preámbulo", article: null);
            }
        }

        // Each heading → next heading (or end of text)
        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;
            int end = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;

            var segment = text[start..end].Trim();
            if (string.IsNullOrWhiteSpace(segment))
                continue;

            var heading = ExtractHeadingLabel(matches[i].Value);
            var (section, article) = ClassifyHeading(heading);

            AddSegment(result, segment, baseMetadata, section, article);
        }

        return result;
    }

    /// <summary>
    /// Adds one or more <see cref="DocumentChunk"/>s for a segment.
    /// If the segment exceeds <see cref="MaxTokensPerChunk"/> it is sub-chunked.
    /// </summary>
    private void AddSegment(
        List<DocumentChunk> target,
        string segment,
        ChunkMetadata baseMetadata,
        string? section,
        string? article)
    {
        int tokenCount = Encoding.Encode(segment).Count;

        if (tokenCount <= MaxTokensPerChunk)
        {
            target.Add(CreateChunk(segment, baseMetadata, section, article));
            return;
        }

        // Sub-chunk oversized segment, preserving the heading metadata
        var subTexts = TokenSplitRaw(segment);
        int part = 1;

        foreach (var sub in subTexts)
        {
            var partLabel = subTexts.Count > 1 ? $"{section ?? article} (parte {part})" : section;
            target.Add(CreateChunk(sub, baseMetadata, section: partLabel, article));
            part++;
        }
    }

    // ── Token-based splitting ────────────────────────────────────────

    private List<DocumentChunk> TokenSplit(string text, ChunkMetadata baseMetadata)
    {
        var rawChunks = TokenSplitRaw(text);
        var result = new List<DocumentChunk>(rawChunks.Count);

        for (int i = 0; i < rawChunks.Count; i++)
        {
            result.Add(CreateChunk(
                rawChunks[i],
                baseMetadata,
                section: $"Chunk {i + 1}/{rawChunks.Count}",
                article: null));
        }

        return result;
    }

    /// <summary>
    /// Pure token-based sliding window split. Returns raw text segments.
    /// </summary>
    private static List<string> TokenSplitRaw(string text)
    {
        var tokens = Encoding.Encode(text);
        var result = new List<string>();
        int stride = MaxTokensPerChunk - OverlapTokens;

        for (int i = 0; i < tokens.Count; i += stride)
        {
            var slice = tokens.Skip(i).Take(MaxTokensPerChunk).ToList();
            if (slice.Count == 0)
                break;

            result.Add(Encoding.Decode(slice));
        }

        return result;
    }

    // ── Heading classification ───────────────────────────────────────

    /// <summary>
    /// Cleans whitespace from the matched heading text.
    /// </summary>
    private static string ExtractHeadingLabel(string raw) =>
        raw.Trim().Replace("  ", " ");

    /// <summary>
    /// Maps a heading label to (<c>section</c>, <c>article</c>) tuple.
    /// Articles populate the <c>article</c> field; everything else goes to <c>section</c>.
    /// </summary>
    private static (string? Section, string? Article) ClassifyHeading(string heading)
    {
        bool isArticle = heading.StartsWith("Art", StringComparison.OrdinalIgnoreCase)
                      || heading.StartsWith("ARTICLE", StringComparison.OrdinalIgnoreCase);

        return isArticle
            ? (Section: null, Article: heading)
            : (Section: heading, Article: null);
    }

    // ── Factory ──────────────────────────────────────────────────────

    private static DocumentChunk CreateChunk(
        string text,
        ChunkMetadata baseMetadata,
        string? section,
        string? article)
    {
        return new DocumentChunk
        {
            Text = text,
            Metadata = baseMetadata with
            {
                Section = section ?? baseMetadata.Section,
                Article = article ?? baseMetadata.Article
            }
        };
    }
}
