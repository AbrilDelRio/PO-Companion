using DC.CopyProyectFromTemplate.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using net.sf.mpxj;
using net.sf.mpxj.MpxjUtilities;
using net.sf.mpxj.reader;
using ProjectTask = net.sf.mpxj.Task;

namespace DC.CopyProyectFromTemplate.Services;

public sealed class MppProjectReader
{
    private static readonly byte[] CompoundFileSignature =
    {
        0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1
    };

    private readonly ILogger<MppProjectReader> logger;
    private readonly TimeZoneInfo sourceTimeZone;

    public MppProjectReader(
        ILogger<MppProjectReader> logger,
        IConfiguration configuration)
    {
        this.logger = logger;

        string sourceTimeZoneId = configuration["MppSourceTimeZone"]?.Trim()
            ?? TimeZoneInfo.Utc.Id;

        try
        {
            sourceTimeZone = TimeZoneInfo.FindSystemTimeZoneById(sourceTimeZoneId);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"MppSourceTimeZone '{sourceTimeZoneId}' is not a valid time-zone identifier.",
                ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new InvalidOperationException(
                $"MppSourceTimeZone '{sourceTimeZoneId}' is invalid.",
                ex);
        }
    }

    public MppPlan Read(string filePath)
    {
        ValidateCompoundFileSignature(filePath);

        ProjectFile project;

        try
        {
            project = new UniversalProjectReader().read(filePath);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                "The uploaded file could not be read as a Microsoft Project MPP file.",
                ex);
        }

        ProjectProperties properties = project.getProjectProperties();
        decimal hoursPerWorkingDay = Math.Max(
            1m,
            Convert.ToDecimal(properties.getMinutesPerDay()?.intValue() ?? 480) / 60m);

        MppPlan plan = new MppPlan
        {
            ProjectTitle = properties.getProjectTitle(),
            ProjectStart = NormalizeDate(properties.getStartDate().ToNullableDateTime()),
            ProjectFinish = NormalizeDate(properties.getFinishDate().ToNullableDateTime()),
            HoursPerWorkingDay = hoursPerWorkingDay
        };

        Dictionary<int, ProjectTask> sourceTasks = new();

        foreach (ProjectTask task in project.getTasks()
                     .ToIEnumerable<ProjectTask>()
                     .OrderBy(value => value.getID()?.intValue() ?? int.MaxValue)
                     .ThenBy(value => value.getUniqueID()?.intValue() ?? int.MaxValue))
        {
            if (task.getID()?.intValue() == 0)
            {
                continue;
            }

            int sourceId = task.getID()?.intValue()
                ?? throw new InvalidDataException("An MPP task does not contain an ID.");

            int sourceUniqueId = task.getUniqueID()?.intValue() ?? sourceId;
            if (sourceUniqueId <= 0)
            {
                throw new InvalidDataException(
                    $"MPP task ID {sourceId} does not contain a valid unique ID.");
            }

            if (!sourceTasks.TryAdd(sourceUniqueId, task))
            {
                throw new InvalidDataException(
                    $"The MPP file contains duplicate task unique ID {sourceUniqueId}.");
            }

            int? parentUniqueId = task.getParentTask()?.getUniqueID()?.intValue();
            if (parentUniqueId == 0)
            {
                parentUniqueId = null;
            }

            decimal durationHours = ToHours(task.getDuration(), properties);
            decimal effortHours = ToHours(task.getWork(), properties);

            plan.Tasks.Add(new MppTaskDefinition
            {
                SourceId = sourceId,
                SourceUniqueId = sourceUniqueId,
                ParentSourceUniqueId = parentUniqueId,
                Name = string.IsNullOrWhiteSpace(task.getName())
                    ? $"Task {sourceId}"
                    : task.getName().Trim(),
                Notes = string.IsNullOrWhiteSpace(task.getNotes()) ? null : task.getNotes(),
                OutlineLevel = Math.Max(1, task.getOutlineLevel()?.intValue() ?? 1),
                OutlineNumber = task.getOutlineNumber(),
                IsSummary = task.getSummary(),
                IsMilestone = task.getMilestone(),
                IsCritical = task.getCritical(),
                IsActive = task.getActive(),
                Start = NormalizeDate(task.getStart().ToNullableDateTime()),
                Finish = NormalizeDate(task.getFinish().ToNullableDateTime()),
                DurationHours = Math.Max(0m, durationHours),
                EffortHours = Math.Max(0m, effortHours),
                PercentageComplete = task.getPercentageComplete()?.ToNullableDouble()
            });
        }

        HashSet<int> importedUniqueIds = plan.Tasks
            .Select(task => task.SourceUniqueId)
            .ToHashSet();

        HashSet<string> dependencyKeys = new(StringComparer.Ordinal);

        foreach (MppTaskDefinition taskDefinition in plan.Tasks)
        {
            ProjectTask task = sourceTasks[taskDefinition.SourceUniqueId];

            foreach (Relation relation in task.getPredecessors().ToIEnumerable<Relation>())
            {
                int? predecessorId = relation.getPredecessorTask()?.getUniqueID()?.intValue();
                int? successorId = relation.getSuccessorTask()?.getUniqueID()?.intValue()
                    ?? task.getUniqueID()?.intValue();

                if (!predecessorId.HasValue ||
                    !successorId.HasValue ||
                    !importedUniqueIds.Contains(predecessorId.Value) ||
                    !importedUniqueIds.Contains(successorId.Value))
                {
                    plan.SkippedExternalDependencies++;
                    continue;
                }

                MppDependencyType type = MapDependencyType(relation.getType());
                decimal lagHours = ToHours(relation.getLag(), properties);

                string key = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{predecessorId.Value}|{successorId.Value}|{(int)type}|{lagHours}");

                if (!dependencyKeys.Add(key))
                {
                    continue;
                }

                plan.Dependencies.Add(new MppDependencyDefinition
                {
                    PredecessorSourceUniqueId = predecessorId.Value,
                    SuccessorSourceUniqueId = successorId.Value,
                    Type = type,
                    LagHours = lagHours
                });
            }
        }

        ReadResourcesAndAssignments(project, plan, properties);

        logger.LogInformation(
            "Read MPP plan with {TaskCount} tasks, {DependencyCount} internal dependencies, " +
            "{ResourceCount} resources and {AssignmentCount} assignments. Source time zone: {SourceTimeZone}.",
            plan.Tasks.Count,
            plan.Dependencies.Count,
            plan.Resources.Count,
            plan.Assignments.Count,
            sourceTimeZone.Id);

        return plan;
    }

    /// <summary>
    /// Reads resources and their assignments. Project always carries a phantom resource with
    /// unique ID 0 and an empty name, and assignments whose resource unique ID is null are the
    /// implicit work rows Project keeps for unassigned tasks; neither is importable.
    /// </summary>
    private void ReadResourcesAndAssignments(ProjectFile project, MppPlan plan, ProjectProperties properties)
    {
        HashSet<int> importedResourceIds = new();

        foreach (Resource resource in project.getResources().ToIEnumerable<Resource>())
        {
            int uniqueId = resource.getUniqueID()?.intValue() ?? 0;
            string name = resource.getName()?.Trim() ?? string.Empty;

            if (uniqueId <= 0 || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!importedResourceIds.Add(uniqueId))
            {
                continue;
            }

            string? email = resource.getEmailAddress()?.Trim();

            plan.Resources.Add(new MppResourceDefinition
            {
                SourceUniqueId = uniqueId,
                Name = name,
                EmailAddress = string.IsNullOrWhiteSpace(email) ? null : email,
                ResourceType = resource.getType()?.toString()
            });
        }

        HashSet<int> importedTaskIds = plan.Tasks.Select(task => task.SourceUniqueId).ToHashSet();
        HashSet<string> assignmentKeys = new(StringComparer.Ordinal);

        foreach (ResourceAssignment assignment in project.getResourceAssignments().ToIEnumerable<ResourceAssignment>())
        {
            int? taskUniqueId = assignment.getTaskUniqueID()?.intValue();
            int? resourceUniqueId = assignment.getResourceUniqueID()?.intValue();

            if (!resourceUniqueId.HasValue ||
                resourceUniqueId.Value <= 0 ||
                !importedResourceIds.Contains(resourceUniqueId.Value))
            {
                plan.SkippedAssignmentsWithoutResource++;
                continue;
            }

            if (!taskUniqueId.HasValue || !importedTaskIds.Contains(taskUniqueId.Value))
            {
                plan.SkippedAssignmentsWithoutResource++;
                continue;
            }

            if (!assignmentKeys.Add($"{taskUniqueId.Value}|{resourceUniqueId.Value}"))
            {
                continue;
            }

            plan.Assignments.Add(new MppAssignmentDefinition
            {
                TaskSourceUniqueId = taskUniqueId.Value,
                ResourceSourceUniqueId = resourceUniqueId.Value,
                Start = NormalizeDate(assignment.getStart().ToNullableDateTime()),
                Finish = NormalizeDate(assignment.getFinish().ToNullableDateTime()),
                WorkHours = Math.Max(0m, ToHours(assignment.getWork(), properties)),
                Units = assignment.getUnits()?.ToNullableDouble()
            });
        }
    }

    private static decimal ToHours(Duration? duration, ProjectProperties properties)
    {
        if (duration == null)
        {
            return 0m;
        }

        try
        {
            Duration converted = duration.convertUnits(TimeUnit.HOURS, properties);
            return decimal.Round(Convert.ToDecimal(converted.getDuration()), 6);
        }
        catch (Exception)
        {
            return decimal.Round(Convert.ToDecimal(duration.getDuration()), 6);
        }
    }

    private static MppDependencyType MapDependencyType(RelationType? type)
    {
        return type?.toString() switch
        {
            "SS" => MppDependencyType.StartToStart,
            "FF" => MppDependencyType.FinishToFinish,
            "SF" => MppDependencyType.StartToFinish,
            _ => MppDependencyType.FinishToStart
        };
    }

    private DateTime? NormalizeDate(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        DateTime sourceValue = DateTime.SpecifyKind(
            value.Value,
            DateTimeKind.Unspecified);

        if (sourceTimeZone.IsInvalidTime(sourceValue))
        {
            throw new InvalidDataException(
                $"MPP date {sourceValue:yyyy-MM-dd HH:mm:ss} falls in an invalid daylight-saving " +
                $"time for zone '{sourceTimeZone.Id}'.");
        }

        return TimeZoneInfo.ConvertTimeToUtc(sourceValue, sourceTimeZone);
    }

    private static void ValidateCompoundFileSignature(string filePath)
    {
        using FileStream stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        if (stream.Length < CompoundFileSignature.Length)
        {
            throw new InvalidDataException("The uploaded MPP file is empty or truncated.");
        }

        Span<byte> signature = stackalloc byte[CompoundFileSignature.Length];
        stream.ReadExactly(signature);

        if (!signature.SequenceEqual(CompoundFileSignature))
        {
            throw new InvalidDataException(
                "The uploaded file is not a supported binary Microsoft Project MPP file.");
        }
    }
}
