using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using com.sun.source.util;
using DC.CopyProyectFromTemplate.Models;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectFromTemplate.Services;

public sealed class ExcelPlanDataverseImporter
{
    private const int FinishToStart = 192350000;
    private const int StartToStart = 192350001;
    private const int FinishToFinish = 192350002;
    private const int StartToFinish = 192350004;

    private readonly DataverseConnectionFactory connectionFactory;
    private readonly ILogger<ExcelPlanDataverseImporter> logger;

    public ExcelPlanDataverseImporter(DataverseConnectionFactory connectionFactory, ILogger<ExcelPlanDataverseImporter> logger)
    {
        this.connectionFactory = connectionFactory;
        this.logger = logger;
    }

    public ExcelPlanImportOutcome Import(MppPlan plan,Guid importId,Guid targetProjectId,ResolvedDataverseEnvironment? environment,CancellationToken cancellationToken)
    {
        using ServiceClient serviceClient = connectionFactory.CreateClient(environment);
        IOrganizationService service = serviceClient;
        Helper helper = new Helper(service, connectionFactory.ResolveUrl(environment));
        // No catch here: the failure row is written once, by ImportExcelToProjectService, which
        // keeps its connection open until its own catch runs. Writing it here as well duplicated it.
        TryWriteLog(helper, "Import Started", true);

        MppImportValidator.Validate(plan);
        Entity targetProject = service.Retrieve("msdyn_project",targetProjectId,new ColumnSet("msdyn_subject", "msdyn_scheduledstart"));

        EntityReference targetProjectReference = targetProject.ToEntityReference();

        DateTime? targetProjectStart = targetProject.GetAttributeValue<DateTime?>("msdyn_scheduledstart");
        BusinessApplication businessApplication = new PoConfiguration(service).getBusinessApplication(targetProject);

        Dictionary<int, Guid> taskIds = plan.Tasks.ToDictionary(task => task.SourceUniqueId,task => CreateDeterministicGuid(importId,$"excel-task|{task.SourceUniqueId}"));

        List<PlannedDependency> plannedDependencies = plan.Dependencies.Select(dependency => new PlannedDependency(dependency,
                CreateDeterministicGuid(importId,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"excel-dependency|{dependency.PredecessorSourceUniqueId}|" +
                        $"{dependency.SuccessorSourceUniqueId}|{(int)dependency.Type}|" +
                        $"{dependency.LagHours}")))).ToList();

        ExistingSchedule existing = RetrieveExistingSchedule(service,targetProjectId,taskIds.Values.ToHashSet(),plannedDependencies.Select(item => item.Id).ToHashSet(),businessApplication);

        EntityReference bucketReference = GetOrCreateProjectBucket(service,targetProjectReference);

        DataverseEntitySchema taskSchema = new DataverseEntitySchema(service,"msdyn_projecttask");

        DataverseEntitySchema dependencySchema = new DataverseEntitySchema(service,"msdyn_projecttaskdependency");

        BatchWriter writer = new BatchWriter(service, 50);
        int namesTruncated = 0;
        int tasksCreated = 0;
        int dependenciesCreated = 0;

        int currentMaximumSequence = businessApplication == BusinessApplication.Psa ? existing.MaximumWbsId : existing.MaximumDisplaySequence;

        Dictionary<int, int> taskSequences = plan.Tasks.OrderBy(task => task.SourceId).ThenBy(task => task.SourceUniqueId)
            .Select((task, index) => new
            {
                task.SourceUniqueId,
                Sequence = checked(currentMaximumSequence + index + 1)
            })
            .ToDictionary(item => item.SourceUniqueId, item => item.Sequence);

        foreach (MppTaskDefinition sourceTask in plan.Tasks.OrderBy(task => task.OutlineLevel).ThenBy(task => task.SourceId).ThenBy(task => task.SourceUniqueId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            Guid taskId = taskIds[sourceTask.SourceUniqueId];
            if (existing.CurrentImportTaskIds.Contains(taskId))
            {
                continue;
            }

            Entity task = BuildTask(sourceTask,taskIds,targetProjectReference,bucketReference,taskSchema,targetProjectStart,plan.HoursPerWorkingDay,taskSequences[sourceTask.SourceUniqueId],businessApplication,out bool nameTruncated);

            task.Id = taskId;
            writer.Create(task);
            tasksCreated++;

            if (nameTruncated)
            {
                namesTruncated++;
            }
        }

        writer.Flush();

        foreach (PlannedDependency planned in plannedDependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existing.CurrentImportDependencyIds.Contains(planned.Id))
            {
                continue;
            }

            Entity dependency = BuildDependency(planned.Definition,taskIds,targetProjectReference,dependencySchema,plan.HoursPerWorkingDay);

            dependency.Id = planned.Id;
            writer.Create(dependency);
            dependenciesCreated++;
        }

        writer.Flush();

        string projectName = targetProject.GetAttributeValue<string>("msdyn_subject")?? targetProjectId.ToString();

        //logger.LogInformation("Excel import {ImportId} completed for project {TargetProjectId}. Created {TaskCount} tasks and {DependencyCount} dependencies.",importId,targetProjectId,tasksCreated,dependenciesCreated);

        return new ExcelPlanImportOutcome(projectName,tasksCreated,dependenciesCreated,namesTruncated);
    }

    /// <summary>
    /// The log table is a convenience, not a dependency: a missing mfd_pocopyprojectlog or a
    /// missing privilege on it must never abort an import or mask a real error.
    /// </summary>
    private void TryWriteLog(Helper helper, string description, bool isCompleted)
    {
        try
        {
            helper.createLog(description, isCompleted, null, null, true);
        }
        catch (Exception logException)
        {
            logger.LogWarning(
                logException,
                "The row could not be written to mfd_pocopyprojectlog: {Description}",
                description);
        }
    }

    private static Entity BuildTask(MppTaskDefinition source,IReadOnlyDictionary<int, Guid> taskIds,EntityReference targetProject,EntityReference bucket,DataverseEntitySchema schema,DateTime? targetProjectStart,decimal hoursPerWorkingDay,int taskSequence,BusinessApplication businessApplication,out bool nameTruncated)
    {
        try
        {
            Entity task = new Entity("msdyn_projecttask")
            {
                ["msdyn_project"] = targetProject
            };

            if (!schema.SetString(task, "msdyn_subject", source.Name, out nameTruncated))
            {
                throw new InvalidOperationException("The msdyn_subject task field is not available for create operations.");
            }

            if (source.ParentSourceUniqueId.HasValue)
            {
                task["msdyn_parenttask"] = new EntityReference("msdyn_projecttask", taskIds[source.ParentSourceUniqueId.Value]);
            }

            decimal safeHoursPerDay = hoursPerWorkingDay <= 0m ? 8m : hoursPerWorkingDay;

            decimal durationDays = source.DurationHours / safeHoursPerDay;
            DateTime start = source.Start ?? targetProjectStart ?? throw new InvalidDataException($"Task '{source.Name}' has no start date and the target project has no start date.");

            DateTime finish = source.Finish ?? start.AddDays(Convert.ToDouble(durationDays));

            if (source.IsMilestone)
            {
                finish = start;
                durationDays = 0m;
            }

            schema.SetValue(task, "msdyn_scheduledstart", start);
            schema.SetValue(task, "msdyn_scheduledend", finish);
            schema.SetValue(task, "msdyn_duration", Convert.ToDouble(durationDays));
            schema.SetValue(task, "msdyn_ismilestone", source.IsMilestone);

            if (businessApplication == BusinessApplication.Psa)
            {
                if (!schema.SetString(task, "msdyn_wbsid", taskSequence.ToString(CultureInfo.InvariantCulture), out _))
                {
                    throw new InvalidOperationException("The msdyn_wbsid task field is not available for create operations in PSA.");
                }

                if (!schema.SetValue(task, "msdyn_autoscheduling", false))
                {
                    throw new InvalidOperationException("The msdyn_autoscheduling task field is not available for create operations in PSA.");
                }
            }
            else
            {
                task["msdyn_projectbucket"] = bucket;
                schema.SetValue(task, "msdyn_start", start);
                schema.SetValue(task, "msdyn_finish", finish);
                schema.SetValue(task, "msdyn_displaysequence", taskSequence);
                schema.SetValue(task, "msdyn_outlinelevel", source.OutlineLevel);
                schema.SetValue(task, "msdyn_summary", source.IsSummary);
            }

            schema.SetString(task,"msdyn_msprojectclientid",source.SourceUniqueId.ToString(CultureInfo.InvariantCulture),out _);
            schema.SetString(task, "msdyn_description", source.Notes, out _);

            if (!string.IsNullOrWhiteSpace(source.Notes))
            {
                // if (!schema.SetString(task,"msdyn_descriptionplaintext",source.Notes,out _))
                // {
                // }
            }

            return task;
        }
        catch (Exception)
        {
            throw;
        }
    }

    private static Entity BuildDependency(
        MppDependencyDefinition source,
        IReadOnlyDictionary<int, Guid> taskIds,
        EntityReference targetProject,
        DataverseEntitySchema schema,
        decimal hoursPerWorkingDay)
    {
        Entity dependency = new Entity("msdyn_projecttaskdependency")
        {
            ["msdyn_project"] = targetProject,
            ["msdyn_predecessortask"] = new EntityReference(
                "msdyn_projecttask",
                taskIds[source.PredecessorSourceUniqueId]),
            ["msdyn_successortask"] = new EntityReference(
                "msdyn_projecttask",
                taskIds[source.SuccessorSourceUniqueId])
        };

        schema.SetValue(dependency, "msdyn_linktype", source.Type switch
        {
            MppDependencyType.StartToStart => StartToStart,
            MppDependencyType.FinishToFinish => FinishToFinish,
            MppDependencyType.StartToFinish => StartToFinish,
            _ => FinishToStart
        });

        decimal lagDays = source.LagHours / (hoursPerWorkingDay <= 0m
            ? 8m
            : hoursPerWorkingDay);

        schema.SetValue(dependency, "msdyn_lag", lagDays);

        return dependency;
    }

    private static EntityReference GetOrCreateProjectBucket(
        IOrganizationService service,
        EntityReference project)
    {
        QueryExpression query = new QueryExpression("msdyn_projectbucket")
        {
            ColumnSet = new ColumnSet(false),
            TopCount = 1,
            NoLock = true
        };

        query.Criteria.AddCondition(
            "msdyn_project",
            ConditionOperator.Equal,
            project.Id);

        query.AddOrder("createdon", OrderType.Ascending);

        Entity? existingBucket = service.RetrieveMultiple(query).Entities.FirstOrDefault();
        if (existingBucket != null)
        {
            return existingBucket.ToEntityReference();
        }

        Entity bucket = new Entity("msdyn_projectbucket")
        {
            ["msdyn_project"] = project,
            ["msdyn_name"] = "Bucket 1"
        };

        bucket.Id = service.Create(bucket);
        return bucket.ToEntityReference();
    }

    private static ExistingSchedule RetrieveExistingSchedule(
        IOrganizationService service,
        Guid targetProjectId,
        HashSet<Guid> plannedTaskIds,
        HashSet<Guid> plannedDependencyIds,
        BusinessApplication businessApplication)
    {
        QueryExpression taskQuery = new QueryExpression("msdyn_projecttask")
        {
            ColumnSet = businessApplication == BusinessApplication.Psa
                ? new ColumnSet("msdyn_wbsid")
                : new ColumnSet("msdyn_displaysequence"),
            NoLock = true
        };

        taskQuery.Criteria.AddCondition(
            "msdyn_project",
            ConditionOperator.Equal,
            targetProjectId);

        List<Entity> allTasks = QueryHelper.RetrieveAll(service, taskQuery);
        List<Entity> preexistingTasks = allTasks
            .Where(task => !plannedTaskIds.Contains(task.Id))
            .ToList();

        HashSet<Guid> currentImportTaskIds = allTasks
            .Where(task => plannedTaskIds.Contains(task.Id))
            .Select(task => task.Id)
            .ToHashSet();

        int maximumDisplaySequence = 0;
        int maximumWbsId = 0;
        foreach (Entity task in preexistingTasks)
        {
            if (businessApplication == BusinessApplication.Psa)
            {
                string? wbsId = task.GetAttributeValue<string>("msdyn_wbsid");
                if (int.TryParse(
                        wbsId,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsedWbsId))
                {
                    maximumWbsId = Math.Max(maximumWbsId, parsedWbsId);
                }

                continue;
            }

            if (!task.Attributes.TryGetValue(
                    "msdyn_displaysequence",
                    out object? value) ||
                value == null)
            {
                continue;
            }

            maximumDisplaySequence = Math.Max(
                maximumDisplaySequence,
                Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }

        QueryExpression dependencyQuery = new QueryExpression("msdyn_projecttaskdependency")
        {
            ColumnSet = new ColumnSet(false),
            NoLock = true
        };

        dependencyQuery.Criteria.AddCondition(
            "msdyn_project",
            ConditionOperator.Equal,
            targetProjectId);

        List<Entity> allDependencies = QueryHelper.RetrieveAll(service, dependencyQuery);
        HashSet<Guid> currentImportDependencyIds = allDependencies
            .Where(dependency => plannedDependencyIds.Contains(dependency.Id))
            .Select(dependency => dependency.Id)
            .ToHashSet();

        return new ExistingSchedule(
            maximumDisplaySequence,
            maximumWbsId,
            currentImportTaskIds,
            currentImportDependencyIds);
    }

    private static Guid CreateDeterministicGuid(Guid importId, string discriminator)
    {
        byte[] source = Encoding.UTF8.GetBytes($"{importId:N}|{discriminator}");
        byte[] hash = SHA256.HashData(source);
        Span<byte> guidBytes = hash.AsSpan(0, 16);

        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }

    private sealed record PlannedDependency(
        MppDependencyDefinition Definition,
        Guid Id);

    private sealed record ExistingSchedule(
        int MaximumDisplaySequence,
        int MaximumWbsId,
        HashSet<Guid> CurrentImportTaskIds,
        HashSet<Guid> CurrentImportDependencyIds);
}

public sealed record ExcelPlanImportOutcome(
    string ProjectName,
    int TasksCreated,
    int DependenciesCreated,
    int NamesTruncated);
