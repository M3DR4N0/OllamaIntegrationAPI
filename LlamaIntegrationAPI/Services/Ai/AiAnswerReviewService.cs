using LlamaIntegrationAPI.Models.Ai;
using Microsoft.Extensions.Options;

namespace LlamaIntegrationAPI.Services.Ai;

public class AiAnswerReviewService(
    IAiGatewayService aiGatewayService,
    IOptionsMonitor<AiOptions> optionsMonitor,
    ILogger<AiAnswerReviewService> logger) : IAiAnswerReviewService
{
    public async Task<AiAnswerReviewResult> ReviewAnswerAsync(
        string userRequest,
        string draftAnswer,
        string scenario,
        bool forceSpanish = true,
        string? additionalContext = null,
        string? externalProvider = null,
        string? externalModel = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(draftAnswer))
        {
            return new AiAnswerReviewResult
            {
                FinalAnswer = draftAnswer,
                OllamaAnswer = draftAnswer
            };
        }

        try
        {
            var reviewResponse = await aiGatewayService.GenerateAsync(
                new AiGenerateRequest
                {
                    Task = "review_and_finalize_response",
                    Prompt = draftAnswer,
                    Context = BuildContext(userRequest, scenario, additionalContext),
                    Provider = externalProvider,
                    Model = externalModel,
                    ForceSpanish = forceSpanish,
                    MaxTokens = ResolveReviewMaxTokens(),
                    SystemInstruction =
                        "Review the draft response against the original user request. " +
                        "Verify that the answer is aligned with what was requested, that it does not invent facts, " +
                        "and that it respects the requested output format. " +
                        "If the request asks for Spanish or ForceSpanish is enabled, return the final answer in clear Spanish. " +
                        "Preserve legal meaning, technical data, names, clauses, commands, paths, code, numbers, and exact values. " +
                        "Improve clarity and structure only when supported by the draft answer and context. " +
                        "If the draft answer is incomplete, state the limitation clearly without inventing missing information. " +
                        "If the user implicitly or explicitly asked for JSON, return valid JSON only."
                },
                cancellationToken).ConfigureAwait(false);

            if (!reviewResponse.Success || string.IsNullOrWhiteSpace(reviewResponse.Text))
            {
                logger.LogWarning(
                    "AI answer review failed for scenario '{Scenario}'. Returning original draft. Provider: {Provider}. Error: {Error}",
                    scenario,
                    reviewResponse.Provider,
                    reviewResponse.Error);
                return new AiAnswerReviewResult
                {
                    FinalAnswer = draftAnswer,
                    OllamaAnswer = draftAnswer
                };
            }

            return new AiAnswerReviewResult
            {
                FinalAnswer = reviewResponse.Text,
                OllamaAnswer = draftAnswer,
                GeminiAnswer = reviewResponse.Text
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "AI answer review failed unexpectedly for scenario '{Scenario}'. Returning original draft.",
                scenario);
            return new AiAnswerReviewResult
            {
                FinalAnswer = draftAnswer,
                OllamaAnswer = draftAnswer
            };
        }
    }

    private int ResolveReviewMaxTokens()
    {
        var options = optionsMonitor.CurrentValue;

        if (options.ReviewMaxTokens > 0)
            return options.ReviewMaxTokens;

        return options.MaxTokens > 0 ? options.MaxTokens : 4096;
    }

    private static string BuildContext(string userRequest, string scenario, string? additionalContext)
    {
        var sections = new List<string>
        {
            $"Scenario:\n{scenario}",
            $"Original user request:\n{userRequest}"
        };

        if (!string.IsNullOrWhiteSpace(additionalContext))
            sections.Add($"Additional context:\n{additionalContext.Trim()}");

        return string.Join("\n\n", sections);
    }
}
