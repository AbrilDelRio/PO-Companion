using DC.CopyProyectFromTemplate.Models;

namespace DC.CopyProyectFromTemplate.Services;

internal static class MppImportValidator
{
    public static void Validate(MppPlan plan)
    {
        List<string> errors = new();

        if (plan.Tasks.Count == 0)
        {
            errors.Add("The MPP file does not contain any importable tasks.");
        }

        Dictionary<int, MppTaskDefinition> tasksByUniqueId = plan.Tasks
            .ToDictionary(task => task.SourceUniqueId);

        foreach (MppTaskDefinition task in plan.Tasks)
        {
            if (task.ParentSourceUniqueId.HasValue &&
                !tasksByUniqueId.ContainsKey(task.ParentSourceUniqueId.Value))
            {
                errors.Add(
                    $"Task '{task.Name}' references missing parent unique ID " +
                    $"{task.ParentSourceUniqueId.Value}.");
            }

            if (task.Start.HasValue &&
                task.Finish.HasValue &&
                task.Finish.Value < task.Start.Value)
            {
                errors.Add(
                    $"Task '{task.Name}' finishes before it starts " +
                    $"({task.Start.Value:yyyy-MM-dd HH:mm} > {task.Finish.Value:yyyy-MM-dd HH:mm}).");
            }
        }

        foreach (MppDependencyDefinition dependency in plan.Dependencies)
        {
            if (dependency.PredecessorSourceUniqueId == dependency.SuccessorSourceUniqueId)
            {
                errors.Add(
                    $"Task unique ID {dependency.PredecessorSourceUniqueId} has a self-dependency.");
            }

        }

        if (errors.Count > 0)
        {
            const int maximumReportedErrors = 12;
            string detail = string.Join(
                Environment.NewLine,
                errors.Take(maximumReportedErrors).Select(error => $"- {error}"));

            if (errors.Count > maximumReportedErrors)
            {
                detail += Environment.NewLine +
                    $"- {errors.Count - maximumReportedErrors:N0} additional validation errors were omitted.";
            }

            throw new InvalidDataException(
                "The MPP file cannot be imported because it contains structural errors:" +
                Environment.NewLine + detail);
        }
    }
}
