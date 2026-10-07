namespace DC.CopyProyectFromTemplate.Models;

public sealed class ImportMppRequest
{
    public Guid ImportId { get; set; }

    public Guid TargetProjectId { get; set; }

    public string ContainerName { get; set; } = string.Empty;

    public string BlobName { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    /// <summary>Environment declared by the caller. Null on the legacy route.</summary>
    public DataverseEnvironmentTarget? Environment { get; set; }

    /// <summary>
    /// The resource decisions the user took in the add-in (multipart field resourceMap). Null
    /// when the field was not sent: that is the name / e-mail matching of older add-ins.
    /// </summary>
    public List<ResourceMapEntry>? ResourceMap { get; set; }
}
