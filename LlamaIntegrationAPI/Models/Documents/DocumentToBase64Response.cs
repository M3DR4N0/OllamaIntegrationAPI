using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Models.Documents;

public sealed class DocumentToBase64Response
{
    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("base64")]
    public string Base64 { get; init; } = string.Empty;
}
