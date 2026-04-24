namespace LlamaIntegrationAPI.Models.Rag;

public class IngestionRequest
{
    public IFormFile File { get; set; } = null!;
    public string DocumentType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}
