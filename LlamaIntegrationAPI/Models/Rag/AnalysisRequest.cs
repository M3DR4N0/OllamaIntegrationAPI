namespace LlamaIntegrationAPI.Models.Rag;

public class AnalysisRequest
{
    public IFormFile ContractFile { get; set; } = null!;
    public string Query { get; set; } = string.Empty;
    public string Model { get; set; } = "mistral";
    public int TopK { get; set; } = 5;
}
