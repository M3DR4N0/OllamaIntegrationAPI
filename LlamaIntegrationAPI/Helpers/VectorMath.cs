namespace LlamaIntegrationAPI.Helpers;

/// <summary>
/// Lightweight vector math utilities for in-memory similarity scoring.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Computes cosine similarity between two vectors.
    /// Returns 0 when either vector has zero magnitude.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        int len = Math.Min(a.Length, b.Length);

        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0f ? dot / denom : 0f;
    }
}
