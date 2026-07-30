namespace LlamaIntegrationAPI.Services.Interfaces;

public interface IDocumentOutputService
{
    GeneratedWordDocument CreateWordDocument(string markdown, string suggestedFileName);
}

public sealed record GeneratedWordDocument(
    byte[] Content,
    string FileName,
    string ContentType);
