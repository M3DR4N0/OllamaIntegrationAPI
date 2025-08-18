namespace OllamaIntegrationAPI.Models
{
    public class OllamaRequest
    {
        public required IFormFile File { get; set; }
        public required string Model { get; set; }
        public required string Prompt { get; set; }
        public required string Format { get; set; }
        public bool Stream { get; set; } = false;
    }
}
