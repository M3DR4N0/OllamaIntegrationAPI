namespace LlamaIntegrationAPI.Models.Ai;

public class NormalizeResponseRequest
{
    public string Text { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = "es";
    public string Tone { get; set; } = "professional";
}
