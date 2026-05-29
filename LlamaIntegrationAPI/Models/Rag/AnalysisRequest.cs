namespace LlamaIntegrationAPI.Models.Rag;

public class AnalysisRequest
{
    /// <summary>Official upload field. Send as multipart/form-data field named "file".</summary>
    public IFormFile? File { get; set; }

    /// <summary>
    /// Legacy alias for <see cref="File"/>. Deprecated — migrate to "file".
    /// </summary>
    [Obsolete("Use 'file' instead. This alias will be removed in a future version.")]
    public IFormFile? ContractFile { get; set; }

    /// <summary>Returns the resolved file, preferring <see cref="File"/> over the legacy <see cref="ContractFile"/>.</summary>
    public IFormFile? ResolvedFile => File ?? ContractFile;

    public string Query { get; set; } = string.Empty;
    public string Model { get; set; } = "gemma3:1b"; 
    public int TopK { get; set; } = 5;
}
