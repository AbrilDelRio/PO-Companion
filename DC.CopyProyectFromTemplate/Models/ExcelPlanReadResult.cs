namespace DC.CopyProyectFromTemplate.Models;

public sealed class ExcelPlanReadResult
{
    public required MppPlan Plan { get; init; }

    public int TruncatedPredecessorRows { get; init; }
}
