namespace LlamaIntegrationAPI.Models.Documents;

public enum DocumentOutputFormat
{
    Json,
    Docx,
    Both
}

public static class DocumentOutputFormatParser
{
    public static DocumentOutputFormat Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DocumentOutputFormat.Json;

        return value.Trim().ToLowerInvariant() switch
        {
            "json" => DocumentOutputFormat.Json,
            "docx" => DocumentOutputFormat.Docx,
            "both" => DocumentOutputFormat.Both,
            _ => DocumentOutputFormat.Json
        };
    }
}
