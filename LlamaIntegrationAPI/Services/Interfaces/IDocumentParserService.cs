namespace LlamaIntegrationAPI.Services.Interfaces;

/// <summary>
/// Extracts raw text from document files (PDF, Word, images).
/// Wraps and extends the existing <see cref="OllamaIntegrationAPI.Services.IDocumentProcessor"/>.
/// </summary>
public interface IDocumentParserService
{
    Task<string> ExtractTextAsync(IFormFile file);
    Task<string> ExtractTextAsync(Stream stream, string contentType);
}
