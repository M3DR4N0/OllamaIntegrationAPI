using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models;
using LlamaIntegrationAPI.Models.Response;
using LlamaIntegrationAPI.Services;
using Microsoft.AspNetCore.Mvc;
using OllamaIntegrationAPI.Helpers;
using OllamaIntegrationAPI.Services;
using OllamaSharp.Models;
using SharpToken;

namespace LlamaIntegrationAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentController : ControllerBase
    {
        private readonly IOllamaService _llamaService;
        private readonly IDocumentProcessor _documentProcessor;
        //private readonly IPayloadBuilder _payloadBuilder;

        public DocumentController(IOllamaService LlamaService, IDocumentProcessor documentProcessor)
        {
            _llamaService = LlamaService;
            _documentProcessor = documentProcessor;
            //_payloadBuilder = payloadBuilder;
        }

        [HttpPost("extract-file")]
        public async Task<IActionResult> ExtractFromFile([FromForm] ExtractFromFileRequest request)
        {
            if (!LlamaRequestValidation.IsValid(request, out var errorMessage))
                return BadRequest(ResponseHandler.Error(errorMessage));

            var text = await _documentProcessor.ProcessAsync(request).ConfigureAwait(false);

            if (string.IsNullOrEmpty(text))
                return  StatusCode(500, ResponseHandler.Error("No se pudo extraer contenido"));

            request.Prompt = $"{request.Prompt}\n\nContenido del documento:\n{text}";

            var promptCtxCount = GptEncoding.GetEncoding("cl100k_base").CountTokens(request.Prompt); 
               
            request.Stream = false;

            request.Options = new RequestOptions
            {
                Temperature = 0,
                NumCtx = promptCtxCount + 2000
            };

            var result = await _llamaService.ExtractInfoAsync(request).ConfigureAwait(false);

            return StatusCode((int)result.StatusCode, result);
        }
    }
}
