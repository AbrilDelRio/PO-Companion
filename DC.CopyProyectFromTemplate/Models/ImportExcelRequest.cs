namespace DC.CopyProyectFromTemplate.Models;

public sealed class ImportExcelRequest
{
    public Guid ImportId { get; set; }

    public Guid TargetProjectId { get; set; }

    public string ContainerName { get; set; } = string.Empty;

    public string BlobName { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    /// <summary>Environment declared by the caller. Null on the legacy route.</summary>
    public DataverseEnvironmentTarget? Environment { get; set; }
}
