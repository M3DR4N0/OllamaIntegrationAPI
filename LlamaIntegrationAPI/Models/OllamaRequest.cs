using OllamaSharp.Models;
using System.Runtime.Serialization;

namespace LlamaIntegrationAPI.Models
{
    public class ExtractFromFileRequest : GenerateRequest
    {
        public IFormFile? File { get; set; }

        public List<IFormFile>? TiffFile { get; set; }

        public new string Suffix { get; set; } = string.Empty;
    }
}
