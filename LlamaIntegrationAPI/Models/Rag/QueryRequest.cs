namespace LlamaIntegrationAPI.Models.Rag;

public class QueryRequest
{
    public string Query { get; set; } = string.Empty;
    public string Model { get; set; } = "gemma3:1b";
    public int TopK { get; set; } = 5;
}
