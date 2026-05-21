using OllamaSharp.Models;
using System.Runtime.Serialization;

namespace LlamaIntegrationAPI.Models
{
    public class ExtractFromFileRequest : GenerateRequest
    {
        /// <summary>Primary file field. Send as multipart/form-data field named "file".</summary>
        public IFormFile? File { get; set; }

        /// <summary>Multiple files (e.g. multi-page TIFFs). Send as multipart/form-data field named "files".</summary>
        public List<IFormFile>? Files { get; set; }

        public new string Suffix { get; set; } = string.Empty;
    }
}
