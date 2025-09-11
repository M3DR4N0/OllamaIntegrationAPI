using System.Text.Json.Serialization;

namespace LlamaIntegrationAPI.Models
{
    public class LlamaRequest
    {
        public IFormFile? File { get; set; }

        public List<IFormFile>? TiffFile { get; set; }

        public string? Model { get; set; }
        public required string Prompt { get; set; }
        public string? Format { get; set; }
        public bool Stream { get; set; } = false;

        [JsonIgnore]
        public object? Payload { get; set; } 

    }
}
