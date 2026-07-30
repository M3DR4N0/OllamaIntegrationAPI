using LlamaIntegrationAPI.Services.Interfaces;

namespace LlamaIntegrationAPI.Services.Implementations;

public class DocumentOutputService : IDocumentOutputService
{
    private const string WordContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public GeneratedWordDocument CreateWordDocument(string markdown, string suggestedFileName)
    {
        var safeBaseName = string.IsNullOrWhiteSpace(suggestedFileName)
            ? "analysis-result"
            : Path.GetFileNameWithoutExtension(suggestedFileName.Trim());

        var content = MarkdownWordConverter.ConvertToDocx(markdown, title: safeBaseName);
        return new GeneratedWordDocument(content, $"{safeBaseName}.docx", WordContentType);
    }
}
