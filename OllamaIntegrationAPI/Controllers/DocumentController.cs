using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OllamaIntegrationAPI.Helpers;
using OllamaIntegrationAPI.Models;
using OllamaIntegrationAPI.Models.Response;
using OllamaIntegrationAPI.Services;
using SharpToken;
using System.Data;
using System.IO;
using System.Net;
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
            if (request.File == null && request.TiffFile == null)
                return BadRequest("El archivo es requerido.");

            using var stream = request.File?.OpenReadStream();

            if (stream is not null)
            {
                //ArgumentNullException.ThrowIfNull(stream);

                stream.Position = 0;
            }
           
            string? documentText = null;
            IEnumerable<string>? uriImages = null;

            var msList = new List<MemoryStream>();

            foreach (var file in request.TiffFile!)
            {
                var ms = new MemoryStream();
                await file.CopyToAsync(ms);

                ms.Position = 0;
                msList.Add(ms);
            }

            var contentType = request.File?.ContentType ?? request.TiffFile?.FirstOrDefault()?.ContentType;

            switch (contentType)
            {
                case "application/msword":
                case "application/vnd.openxmlformats-officedocument.wordprocessingml.document":
                    {
                        documentText = _ocrService.ReadDocx(stream);
                        break;
                    }

                case "application/pdf":
                    {
                        documentText = _ocrService.ExtractTextFromPdfAsync(stream);
                        break;
                    }

                case "image/jpeg":
                case "image/png":
                case "image/tiff":
                case "image/tif":
                    {
                        uriImages = _ocrService.GetUriImages(msList);
                        break;
                    }
                default:
                    throw new Exception("Formato no soportado");                  
            }

            if (request.File?.ContentType == "application/pdf" && string.IsNullOrEmpty(documentText))
            {
                uriImages = _ocrService.GetUriImages(stream);
            }

            if (documentText is null && uriImages is null)
                return StatusCode((int)HttpStatusCode.InternalServerError, ResponseHandler.Error("Error al extaer informacion del documento"));

            if (uriImages is not null)
            {
                var img_content = new List<object>()
                {
                    new { type = "text", text = request.Prompt },
                };

                foreach (var item in uriImages)
                {
                    img_content.Add(new { type = "image_url", image_url = new { url = item } });
                }

                var img_payload = new
                {
                    messages = new object[] {
                        new { role = "system", content = "Eres un abogado experto. Responde claro y con viñetas cuando convenga." },
                        new {
                            role = "user",
                            content = img_content
                        }
                    },          
                };

                request.Payload = img_payload;

                var img_result = await _ollamaService.ExtractContractInfoAsync(request);

                return StatusCode((int)img_result.StatusCode, img_result);
               
            }

            var encoding = GptEncoding.GetEncoding("cl100k_base");
            var tokens = encoding.Encode(documentText+request.Prompt);        

            if (tokens.Count >= 32000)
            {
                List<dynamic> results = new();

                var chunks = TextChunker.ChunkByTokens(documentText!).ToList();

                for (int i = 0; i < chunks.Count; i++)
                {
                    string prompt = TextChunker.BuildPrompt(chunks[i], i, chunks.Count, request.Prompt);

                    request.Prompt = prompt;

                    results.Add(await _ollamaService.ExtractContractInfoAsync(request));

                }

                return Ok(results);
            }

            request.Prompt = $"{request.Prompt}\nContenido del documento:\n{documentText}";

            var payload = new
            {
                messages = new[] {
                    new { role = "system", content = "Eres un abogado experto. Responde claro y con viñetas cuando convenga." },
                    new { role = "user",   content = request.Prompt }  // tu texto: "analiza como abogado..."
                },
                //stream = request.Stream, // true/false
                //n_predict = 256,
                //temperature = 0.7,
                //top_p = 0.9,
                //top_k = 40,
                //repeat_penalty = 1.12
            };

            request.Payload = payload;

            var result = await _ollamaService.ExtractContractInfoAsync(request);

            return StatusCode((int)result.StatusCode, result);
        }
    }
}
