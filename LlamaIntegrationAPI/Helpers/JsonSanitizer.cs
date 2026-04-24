using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaIntegrationAPI.Helpers;

/// <summary>
/// Robust multi-strategy JSON extractor for LLM outputs that may contain
/// markdown fences, extra text, trailing commas, or other non-standard formatting.
/// </summary>
public static class JsonSanitizer
{
    private static readonly JsonDocumentOptions LenientOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Attempts multiple strategies to extract valid JSON from raw LLM output.
    /// Returns <c>null</c> only when all strategies fail.
    /// </summary>
    public static JsonElement? TryExtractJson(string? llmResponse)
    {
        if (string.IsNullOrWhiteSpace(llmResponse))
            return null;

        // Strategy 1: Direct parse (already valid JSON)
        if (TryParse(llmResponse, out var result))
            return result;

        // Strategy 2: Strip markdown fences (```json ... ```)
        var stripped = StripMarkdownFences(llmResponse);
        if (stripped != llmResponse && TryParse(stripped, out result))
            return result;

        // Strategy 3: Extract JSON object with balanced braces
        var extracted = ExtractBalancedJson(stripped, '{', '}');
        if (extracted is not null && TryParse(extracted, out result))
            return result;

        // Strategy 4: Extract JSON array with balanced brackets
        extracted = ExtractBalancedJson(stripped, '[', ']');
        if (extracted is not null && TryParse(extracted, out result))
            return result;

        // Strategy 5: Fix common LLM quirks then retry
        var fixed_ = FixCommonIssues(stripped);
        if (TryParse(fixed_, out result))
            return result;

        // Strategy 6: Fix + extract boundaries
        extracted = ExtractBalancedJson(fixed_, '{', '}');
        if (extracted is not null && TryParse(extracted, out result))
            return result;

        return null;
    }

    /// <summary>
    /// Extracts and deserializes JSON into <typeparamref name="T"/>.
    /// Returns <c>default</c> if extraction or deserialization fails.
    /// </summary>
    public static T? TryExtractJson<T>(string? llmResponse) where T : class
    {
        var element = TryExtractJson(llmResponse);
        if (element is null)
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(element.Value.GetRawText(), SerializerOptions);
        }
        catch
        {
            return default;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static bool TryParse(string json, out JsonElement result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, LenientOptions);
            result = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    private static string StripMarkdownFences(string text)
    {
        // Find ``` anywhere in the text (not just at the start)
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart < 0)
            return text;

        // Skip the language identifier line (e.g., ```json)
        var contentStart = text.IndexOf('\n', fenceStart);
        if (contentStart < 0)
            return text;

        // Find the closing fence
        var fenceEnd = text.IndexOf("```", contentStart, StringComparison.Ordinal);
        if (fenceEnd < 0)
            return text[(contentStart + 1)..].Trim();

        return text[(contentStart + 1)..fenceEnd].Trim();
    }

    /// <summary>
    /// Extracts a balanced JSON fragment by tracking nesting depth,
    /// respecting string literals so embedded braces don't break the count.
    /// </summary>
    private static string? ExtractBalancedJson(string text, char open, char close)
    {
        var start = text.IndexOf(open);
        if (start < 0)
            return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == open) depth++;
            if (c == close) depth--;

            if (depth == 0)
                return text[start..(i + 1)];
        }

        // Unbalanced — fall back to first open → last close
        var lastClose = text.LastIndexOf(close);
        return lastClose > start ? text[start..(lastClose + 1)] : null;
    }

    private static readonly Regex TrailingCommaPattern = new(@",\s*([}\]])", RegexOptions.Compiled);

    private static string FixCommonIssues(string json)
    {
        // Remove trailing commas before } or ]
        json = TrailingCommaPattern.Replace(json, "$1");

        // Replace single quotes with double quotes only when the text
        // has no double-quoted strings (naive heuristic for LLM output)
        if (!json.Contains('"') && json.Contains('\''))
            json = json.Replace('\'', '"');

        return json;
    }
}
