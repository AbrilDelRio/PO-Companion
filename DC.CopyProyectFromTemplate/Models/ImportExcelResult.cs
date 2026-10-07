namespace DC.CopyProyectFromTemplate.Models;

public sealed class ImportExcelResult
{
    public Guid ImportId { get; set; }

    public Guid TargetProjectId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public bool Completed { get; set; }

    public int TasksCreated { get; set; }

    public int DependenciesCreated { get; set; }

    public int InactiveTasksImported { get; set; }

    public int NamesTruncated { get; set; }

    public int DependenciesSkipped { get; set; }

    public int TruncatedPredecessorRows { get; set; }

    public string Message { get; set; } = string.Empty;
}
