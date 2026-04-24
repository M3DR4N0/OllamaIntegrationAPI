using LlamaIntegrationAPI.Services.Interfaces;
using OllamaIntegrationAPI.Services;

namespace LlamaIntegrationAPI.Services.Implementations;

/// <summary>
/// Extracts text from documents by reusing the existing
/// <see cref="DocumentProcessor"/> extraction methods.
/// </summary>
public class DocumentParserService(ILogger<DocumentParserService> logger) : IDocumentParserService
{
    private static readonly HashSet<string> WordTypes =
    [
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
    ];

    private static readonly HashSet<string> ImageTypes =
    [
        "image/jpeg",
        "image/png",
        "image/tiff",
        "image/tif"
    ];

    public async Task<string> ExtractTextAsync(IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        return await ExtractTextAsync(stream, file.ContentType);
    }

    public Task<string> ExtractTextAsync(Stream stream, string contentType)
    {
        logger.LogInformation("Parsing document with ContentType: {ContentType}", contentType);

        var text = contentType switch
        {
            _ when WordTypes.Contains(contentType)
                => DocumentProcessor.ExtractTextFromWord(stream),

            "application/pdf"
                => DocumentProcessor.ExtractTextFromPdf(stream),

            _ when ImageTypes.Contains(contentType)
                => DocumentProcessor.ExtractTextFromImage(stream),

            _ => throw new NotSupportedException($"Unsupported content type: {contentType}")
        };

        return Task.FromResult(text);
    }
}
