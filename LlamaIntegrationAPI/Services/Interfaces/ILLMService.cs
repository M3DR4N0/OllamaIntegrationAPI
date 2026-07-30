namespace LlamaIntegrationAPI.Services.Interfaces;

/// <summary>
/// Abstracts LLM interactions (Ollama / llama.cpp).
/// Wraps the existing <see cref="IOllamaService"/> with a cleaner contract.
/// </summary>
public interface ILLMService
{
    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        string model,
        CancellationToken ct = default,
        int? maxPredict = null);

    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        string model,
        bool requireJson,
        CancellationToken ct = default,
        int? maxPredict = null);

    Task<T?> GenerateAsync<T>(
        string systemPrompt,
        string userPrompt,
        string model,
        CancellationToken ct = default,
        int? maxPredict = null) where T : class;
}
