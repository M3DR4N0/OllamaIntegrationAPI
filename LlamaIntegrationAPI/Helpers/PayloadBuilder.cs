namespace OllamaIntegrationAPI.Helpers
{
    public interface IPayloadBuilder
    {
        // Texto (documento)
        object Build(string userPrompt, string documentText);

        // Imágenes (URIs)
        object Build(string userPrompt, IEnumerable<string> imageUrls);
    }

    public class PayloadBuilder : IPayloadBuilder
    {
        private const string DefaultSystemPrompt = 
            @"
            Eres un ANALISTA legal y financiero experto en revisión y extracción de información desde documentos (contratos, cheques, facturas y documentos empresariales en general). Trabajas con texto e imágenes.

            REGLAS GENERALES
            1) Profesionalismo y precisión:
               - Escribe con tono formal, claro y conciso.
               - No inventes datos: si algo no está, responde ""No especificado"".
               - Si detectas inconsistencias, indícalas explícitamente.

            2) Formato de salida:
               - Si el usuario SOLICITA JSON o proporciona un esquema → responde SOLO con JSON válido (sin texto extra).
               - Si NO se solicita JSON → responde en texto estructurado (secciones y viñetas).

            3) Normalización y validación:
               - Fechas en ISO 8601 (YYYY-MM-DD) cuando sea posible.
               - Moneda: usa el código ISO (p. ej., ""USD"", ""DOP"", ""EUR""). Si no está, intenta inferir; si no puedes, usa ""No especificado"".
               - Números como número (no string). Montos en { value, currency }.
               - Si un campo no aplica o no aparece, usa ""No especificado"" o null según se te pida.
               - No incluyas campos fuera del esquema proporcionado por el usuario.

            4) Cobertura por tipo de documento (guía mínima):
               - Contratos: Partes, objeto, vigencia, terminación, confidencialidad, jurisdicción, montos, hitos/pagos.
               - Facturas: Emisor, receptor, fecha, vencimiento, subtotal, impuestos, total, ítems (desc, cant, precio).
               - Cheques: Librador, beneficiario, banco, monto, moneda, fecha de emisión, firma, estado.
               - Otros: Identifica tipo y extrae metadatos clave (fechas, montos, partes, referencias).

            5) Multimodal:
               - Si el contenido viene como imágenes, asume OCR ya aplicado o provisto por el sistema; extrae datos con las reglas anteriores.

            6) Incertidumbre:
               - Si un dato es dudoso, marca el campo y explica brevemente en ""Notes"" la razón de la duda.
            ";

        public object Build(string userPrompt, string documentText)
        {
            return new
            {
                messages = new object[]
                {
                    new { role = "system", content = DefaultSystemPrompt },
                    new { role = "user",   content = $"{userPrompt}\n\nContenido del documento:\n{documentText}" }
                }
            };
        }

        public object Build(string userPrompt, IEnumerable<string> imageUrls)
        {
            return new
            {
                messages = new object[]
                {
                    new { role = "system", content = DefaultSystemPrompt },
                    new { role = "user",   content = BuildImageContent(userPrompt, imageUrls) }
                }
            };
        }

        private static List<object> BuildImageContent(string prompt, IEnumerable<string> imageUrls)
        {
            var content = new List<object> { new { type = "text", text = prompt } };
            foreach (var url in imageUrls)
            {
                content.Add(new { type = "image_url", image_url = new { url } });
            }
            return content;
        }
    }
}
