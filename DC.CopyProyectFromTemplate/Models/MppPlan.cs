namespace DC.CopyProyectFromTemplate.Models;

public sealed class MppPlan
{
    public string? ProjectTitle { get; set; }

    public DateTime? ProjectStart { get; set; }

    public DateTime? ProjectFinish { get; set; }

    public decimal HoursPerWorkingDay { get; set; } = 8m;

    public List<MppTaskDefinition> Tasks { get; } = new();

    public List<MppDependencyDefinition> Dependencies { get; } = new();

    public List<MppResourceDefinition> Resources { get; } = new();

    public List<MppAssignmentDefinition> Assignments { get; } = new();

    public int SkippedExternalDependencies { get; set; }

    /// <summary>Assignments dropped while reading because they carry no resource.</summary>
    public int SkippedAssignmentsWithoutResource { get; set; }
}

public sealed class MppResourceDefinition
{
    public int SourceUniqueId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? EmailAddress { get; set; }

    /// <summary>MPXJ ResourceType: WORK, MATERIAL or COST.</summary>
    public string? ResourceType { get; set; }
}

public sealed class MppAssignmentDefinition
{
    public int TaskSourceUniqueId { get; set; }

    public int ResourceSourceUniqueId { get; set; }

    public DateTime? Start { get; set; }

    public DateTime? Finish { get; set; }

    public decimal WorkHours { get; set; }

    public double? Units { get; set; }
}

public sealed class MppTaskDefinition
{
    public int SourceId { get; set; }

    public int SourceUniqueId { get; set; }

    public int? ParentSourceUniqueId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public int OutlineLevel { get; set; }

    public string? OutlineNumber { get; set; }

    public bool IsSummary { get; set; }

    public bool IsMilestone { get; set; }

    public bool IsCritical { get; set; }

    public bool IsActive { get; set; }

    public DateTime? Start { get; set; }

    public DateTime? Finish { get; set; }

    public decimal DurationHours { get; set; }

    public decimal EffortHours { get; set; }

    public double? PercentageComplete { get; set; }
}

public sealed class MppDependencyDefinition
{
    public int PredecessorSourceUniqueId { get; set; }

    public int SuccessorSourceUniqueId { get; set; }

    public MppDependencyType Type { get; set; }

    public decimal LagHours { get; set; }
}

public enum MppDependencyType
{
    FinishToStart,
    StartToStart,
    FinishToFinish,
    StartToFinish
}
