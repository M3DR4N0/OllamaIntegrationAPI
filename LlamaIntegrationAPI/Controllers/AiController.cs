using LlamaIntegrationAPI.Models.Ai;
using LlamaIntegrationAPI.Services.Ai;
using Microsoft.AspNetCore.Mvc;

namespace LlamaIntegrationAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AiController(
    IAiGatewayService aiGatewayService,
    ILogger<AiController> logger) : ControllerBase
{
    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] QuickAskRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { success = false, error = "Question is required." });

        logger.LogInformation("POST /api/ai/ask received.");

        var result = await aiGatewayService.GenerateAsync(
            new AiGenerateRequest
            {
                Task = "quick_ask",
                Prompt = request.Question,
                Context = request.Context,
                Provider = request.Provider,
                Model = request.Model,
                ForceSpanish = request.ForceSpanish,
                SystemInstruction =
                    "Respond to quick user questions. Use the provided context if available. " +
                    "If there is not enough information, say so clearly. Keep the answer brief and useful."
            },
            cancellationToken).ConfigureAwait(false);

        return CreateResult(result);
    }

    [HttpPost("normalize")]
    public async Task<IActionResult> Normalize(
        [FromBody] NormalizeResponseRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { success = false, error = "Text is required." });

        logger.LogInformation("POST /api/ai/normalize received.");

        var normalizeInstruction =
            "Normalize the input text for an end user. Detect whether the source text is in English or another language " +
            "and convert it to Spanish when needed. Preserve proper names, file paths, commands, code, error messages, " +
            "and exact values. Improve clarity without changing the meaning. " +
            $"Use a {request.Tone.Trim()} tone. Do not add information that is not present in the source text.";

        var result = await aiGatewayService.GenerateAsync(
            new AiGenerateRequest
            {
                Task = "normalize_response",
                Prompt = request.Text,
                Provider = request.Provider,
                Model = request.Model,
                TargetLanguage = request.TargetLanguage,
                ForceSpanish = string.Equals(request.TargetLanguage, "es", StringComparison.OrdinalIgnoreCase),
                SystemInstruction = normalizeInstruction
            },
            cancellationToken).ConfigureAwait(false);

        return CreateResult(result);
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate(
        [FromBody] AiGenerateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Task))
            return BadRequest(new { success = false, error = "Task is required." });

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { success = false, error = "Prompt is required." });

        logger.LogInformation("POST /api/ai/generate received for task '{Task}'.", request.Task);

        var result = await aiGatewayService.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : StatusCode(StatusCodes.Status502BadGateway, result);
    }

    private IActionResult CreateResult(AiGenerateResponse result)
    {
        var payload = new
        {
            success = result.Success,
            provider = result.Provider,
            model = result.Model,
            text = result.Text,
            durationMs = (long)result.Duration.TotalMilliseconds,
            error = result.Error
        };

        return result.Success
            ? Ok(payload)
            : StatusCode(StatusCodes.Status502BadGateway, payload);
    }
}
