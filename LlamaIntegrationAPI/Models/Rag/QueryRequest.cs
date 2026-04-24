namespace LlamaIntegrationAPI.Models.Rag;

public class QueryRequest
{
    public string Query { get; set; } = string.Empty;
    public string Model { get; set; } = "mistral";
    public int TopK { get; set; } = 5;
}
