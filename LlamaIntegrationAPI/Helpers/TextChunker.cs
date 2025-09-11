using ImageMagick.Drawing;
using SharpToken;

namespace LlamaIntegrationAPI.Helpers
{
    public static class TextChunker
    {
        public static IEnumerable<string> ChunkByTokens(string text, int maxTokensPerChunk = 1200, int overlapTokens = 200)
        {
            var encoding = GptEncoding.GetEncoding("cl100k_base");
            var tokens = encoding.Encode(text);

            for (int i = 0; i < tokens.Count; i += maxTokensPerChunk - overlapTokens)
            {
                var slice = tokens.Skip(i).Take(maxTokensPerChunk).ToList();
                if (slice.Count == 0) break;

                yield return encoding.Decode(slice);
            }
        }

        public static string BuildPrompt(string chunkText, int chunkIndex, int totalChunks, string initialPrompt)
        {
            return $@"
                {initialPrompt}
                Estás procesando un documento que ha sido dividido en partes (chunks).
                Ahora estás viendo el chunk {chunkIndex + 1} de {totalChunks}.

                Texto del chunk:
                \\\
                {chunkText}
                \\\

                Tu tarea:
                1.Analiza solo este chunk.
                2.Extrae información relevante.
                3.No inventes información que no aparezca en este chunk.
            ";
        }
    
    }
}
