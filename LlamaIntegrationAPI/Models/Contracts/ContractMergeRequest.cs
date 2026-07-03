namespace LlamaIntegrationAPI.Models.Contracts;

public class ContractMergeRequest
{
    public const string DefaultQuery =
        "Actua como un Abogado Experto en Redaccion de Contratos y Revisor Legal. " +
        "Tu objetivo es integrar clausulas especificas de manera organica dentro de un borrador de contrato existente.";

    /// <summary>Dos o mas archivos a fusionar. El primer archivo se trata como borrador base por defecto.</summary>
    public List<IFormFile> Files { get; set; } = [];

    /// <summary>Consulta o instruccion funcional para la fusion contractual.</summary>
    public string Query { get; set; } = DefaultQuery;

    [Obsolete("Use 'query' instead. This alias will be removed in a future version.")]
    public string? Prompt { get; set; }

    /// <summary>Modelo local de Ollama que generara la propuesta local.</summary>
    public string Model { get; set; } = "gemma3:4b";

    /// <summary>Proveedor externo opcional para revisar o refinar la respuesta.</summary>
    public string? ExternalProvider { get; set; }

    /// <summary>Modelo externo opcional del proveedor configurado, por ejemplo gemini-2.5-flash.</summary>
    public string? ExternalModel { get; set; }

    /// <summary>Indice del documento base dentro de Files. Por defecto, el primero.</summary>
    public int BaseDocumentIndex { get; set; } = 0;

    public bool ForceSpanish { get; set; } = true;
}
