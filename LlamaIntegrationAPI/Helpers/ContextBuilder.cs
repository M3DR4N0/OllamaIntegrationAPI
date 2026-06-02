using LlamaIntegrationAPI.Models.Rag;
using System.Text;

namespace LlamaIntegrationAPI.Helpers;

/// <summary>
/// Builds enriched LLM prompts by combining the user query with
/// relevant document chunks and retrieved legal/regulatory context.
/// </summary>
public static class ContextBuilder
{
    /// <summary>
    /// Assembles a structured prompt from user query + document chunks + legal context.
    /// </summary>
    public static string Build(
        string userPrompt,
        IReadOnlyList<DocumentChunk> documentChunks,
        IReadOnlyList<DocumentChunk> legalChunks)
    {
        var sb = new StringBuilder();

        sb.AppendLine(userPrompt);
        sb.AppendLine();

        if (documentChunks.Count > 0)
        {
            sb.AppendLine("=== CONTENIDO RELEVANTE DEL DOCUMENTO ===");

            foreach (var chunk in documentChunks)
            {
                var label = chunk.Metadata.Article ?? chunk.Metadata.Section;
                if (!string.IsNullOrWhiteSpace(label))
                    sb.AppendLine($"[{label}]");

                sb.AppendLine(chunk.Text);
                sb.AppendLine();
            }
        }

        if (legalChunks.Count > 0)
        {
            sb.AppendLine("=== CONTEXTO LEGAL / REGULATORIO RELEVANTE ===");

            foreach (var chunk in legalChunks)
            {
                var source = chunk.Metadata.DocumentName;
                var label = chunk.Metadata.Article ?? chunk.Metadata.Section;

                var header = !string.IsNullOrWhiteSpace(label)
                    ? $"[Fuente: {source} — {label}]"
                    : $"[Fuente: {source}]";

                sb.AppendLine(header);
                sb.AppendLine(chunk.Text);
                sb.AppendLine();
            }
        }

        sb.AppendLine("=== INSTRUCCIÓN OBLIGATORIA ===");
        sb.AppendLine("Responde SIEMPRE en español, independientemente del idioma del contexto o los documentos.");

        return sb.ToString();
    }
}
