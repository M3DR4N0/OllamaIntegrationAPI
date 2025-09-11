using LlamaIntegrationAPI.Helpers;
using LlamaIntegrationAPI.Models;
using LlamaIntegrationAPI.Models.Response;
using LlamaIntegrationAPI.Services;
using Microsoft.AspNetCore.Mvc;
using OllamaIntegrationAPI.Helpers;
using OllamaIntegrationAPI.Services;

namespace LlamaIntegrationAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentController : ControllerBase
    {
        private readonly ILlamaService _llamaService;
        private readonly IDocumentProcessor _documentProcessor;
        private readonly IPayloadBuilder _payloadBuilder;

        public DocumentController(ILlamaService LlamaService, IDocumentProcessor documentProcessor, IPayloadBuilder payloadBuilder)
        {
            _llamaService = LlamaService;
            _documentProcessor = documentProcessor;
            _payloadBuilder = payloadBuilder;
        }

        [HttpPost("extract-file")]
        public async Task<IActionResult> ExtractFromFile([FromForm] LlamaRequest request)
        {
            if (!LlamaRequestValidation.IsValid(request, out var errorMessage))
                return BadRequest(ResponseHandler.Error(errorMessage));

            var (text, images) = await _documentProcessor.ProcessAsync(request);

            if (text == null && images == null)
                return  StatusCode(500, ResponseHandler.Error("No se pudo extraer contenido"));

            var payload = images is not null
                ? _payloadBuilder.Build(request.Prompt, images!)
                : _payloadBuilder.Build(request.Prompt, text!);

            request.Payload = payload;

            var result = await _llamaService.ExtractContractInfoAsync(request);

            return StatusCode((int)result.StatusCode, result);
        }
    }
}
