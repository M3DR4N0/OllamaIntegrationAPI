namespace LlamaIntegrationAPI.Models.Rag;

public class MultiDocumentAnalysisRequest
{
    /// <summary>Dos o más archivos a analizar en conjunto.</summary>
    public List<IFormFile> Files { get; set; } = [];

    public string Query { get; set; } = string.Empty;
    public string Model { get; set; } = "gemma3:1b";
    public string? ExternalProvider { get; set; }
    public string? ExternalModel { get; set; }
    public int TopK { get; set; } = 5;
    public bool ForceSpanish { get; set; } = true;
    public bool ReviewWithAi { get; set; } = true;
}
