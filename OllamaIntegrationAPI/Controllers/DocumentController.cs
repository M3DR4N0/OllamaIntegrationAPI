using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using OllamaIntegrationAPI.Models;
using OllamaIntegrationAPI.Services;
using System.Net.Mime;
using System.Xml.Schema;

namespace OllamaIntegrationAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentController : ControllerBase
    {
        private readonly IOllamaService _ollamaService;
        private readonly IOcrService _ocrService;

        public DocumentController(IOllamaService ollamaService, IOcrService ocrService)
        {
            _ollamaService = ollamaService;
            _ocrService = ocrService;
        }

        [HttpPost("extract-file")]
        public async Task<IActionResult> ExtractFromFile([FromForm] OllamaRequest request)
        {
            if (request.File == null || request.File.Length == 0)
                return BadRequest("El archivo es requerido.");

            using var stream = request.File.OpenReadStream();

            string documentText = await _ocrService.ExtractTextAsync(stream, request.File.ContentType);

            request.Prompt = $"{request.Prompt}\nContenido del documento:\n{documentText}";
            var result = await _ollamaService.ExtractContractInfoAsync(request);

            return Ok(result);
        }
    }
}
