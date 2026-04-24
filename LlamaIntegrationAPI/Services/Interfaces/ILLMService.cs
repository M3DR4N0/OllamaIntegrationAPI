namespace LlamaIntegrationAPI.Services.Interfaces;

/// <summary>
/// Abstracts LLM interactions (Ollama / llama.cpp).
/// Wraps the existing <see cref="IOllamaService"/> with a cleaner contract.
/// </summary>
public interface ILLMService
{
    Task<string> GenerateAsync(string systemPrompt, string userPrompt, string model, CancellationToken ct = default);
    Task<T?> GenerateAsync<T>(string systemPrompt, string userPrompt, string model, CancellationToken ct = default) where T : class;
}
