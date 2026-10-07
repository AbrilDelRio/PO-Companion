namespace DC.CopyProyectFromTemplate.Models;

public sealed class ImportMppResult
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

    public int TasksUpdated { get; set; }

    /// <summary>Owned tasks no longer in the .mpp that this run deactivated.</summary>
    public int TasksDeactivated { get; set; }

    /// <summary>Owned tasks back in the .mpp that this run reactivated.</summary>
    public int TasksReactivated { get; set; }

    public int DependenciesUpdated { get; set; }

    /// <summary>Links between tasks of the import that are no longer in the .mpp, deactivated by this run.</summary>
    public int DependenciesDeactivated { get; set; }

    /// <summary>Links back in the .mpp that this run reactivated.</summary>
    public int DependenciesReactivated { get; set; }

    public int ResourcesCreated { get; set; }

    public int ResourcesSkipped { get; set; }

    public int AssignmentsCreated { get; set; }

    public int AssignmentsUpdated { get; set; }

    public int AssignmentsSkipped { get; set; }

    public int AssignmentsDeleted { get; set; }

    /// <summary>
    /// Resources skipped on the resourceMap path, each with its name and the reason, e.g.
    /// "Ana Lopez (unique id 2): it is not in resourceMap". Empty when no resourceMap was sent.
    /// </summary>
    public List<string> SkippedResources { get; set; } = new();

    public string Message { get; set; } = string.Empty;
}
