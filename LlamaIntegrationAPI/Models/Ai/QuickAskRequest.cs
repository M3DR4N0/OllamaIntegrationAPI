namespace LlamaIntegrationAPI.Models.Ai;

public class QuickAskRequest
{
    public string Question { get; set; } = string.Empty;
    public string? Context { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public bool ForceSpanish { get; set; } = true;
}
