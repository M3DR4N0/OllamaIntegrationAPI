namespace LlamaIntegrationAPI.Models.Ai;

public class AiGenerateRequest
{
    public string Task { get; set; } = string.Empty;
    public string? SystemInstruction { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? Context { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? TargetLanguage { get; set; }
    public bool ForceSpanish { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
