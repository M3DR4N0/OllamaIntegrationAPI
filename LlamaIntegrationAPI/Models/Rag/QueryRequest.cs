namespace LlamaIntegrationAPI.Models.Rag;

public class QueryRequest
{
    public string Query { get; set; } = string.Empty;
    public string Model { get; set; } = "gemma3:4b";
    public string? ExternalProvider { get; set; }
    public string? ExternalModel { get; set; }
    public int TopK { get; set; } = 5;
    public bool ForceSpanish { get; set; } = true;
    public bool ReviewWithAi { get; set; } = true;
}
