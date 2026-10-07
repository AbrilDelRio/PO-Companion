using System.Globalization;
using DC.CopyProyectFromTemplate.Models;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectFromTemplate.Services;

public sealed class ImportMppToProjectService
{
    private const int FinishToStart = 192350000;
    private const int StartToStart = 192350001;
    private const int FinishToFinish = 192350002;
    private const int StartToFinish = 192350004;

    /// <summary>
    /// Column that correlates a Dataverse row with the row in the .mpp that produced it. It ships
    /// with Project Operations on msdyn_projecttask, msdyn_projectteam and msdyn_resourceassignment,
    /// so nothing has to be created. A row with this column empty was NOT created by this import
    /// and must never be updated or deleted by it. Tasks cannot be imported without it; where a team
    /// member or assignment table lacks it, those rows are still written but a republish cannot
    /// recognize them.
    /// </summary>
    private const string ClientIdColumn = "msdyn_msprojectclientid";

    private readonly DataverseConnectionFactory connectionFactory;
    private readonly MppBlobStorage blobStorage;
    private readonly MppProjectReader mppReader;
    private readonly ILogger<ImportMppToProjectService> logger;

    public ImportMppToProjectService(DataverseConnectionFactory connectionFactory, MppBlobStorage blobStorage, MppProjectReader mppReader, ILogger<ImportMppToProjectService> logger)
    {
        this.connectionFactory = connectionFactory;
        this.blobStorage = blobStorage;
        this.mppReader = mppReader;
        this.logger = logger;
    }

    public async Task<ImportMppResult> ExecuteAsync(ImportMppRequest input, CancellationToken cancellationToken)
    {
        ValidateInput(input);
        string? localFilePath = null;
        ResolvedDataverseEnvironment? environment = DataverseConnectionFactory.Resolve(input.Environment);
        Helper? helper = null;

        // Declared out here and disposed in the finally, never with a using inside the try: a using
        // declaration disposes the connection as the try block exits, before the catch runs, so
        // TryWriteFailureLog was writing through a closed client and no failure row ever appeared.
        ServiceClient? serviceClient = null;

        try
        {
            // Inside the try on purpose: building the connection is the step most likely to fail,
            // and when it did, the exception used to escape without any log at all.
            logger.LogInformation(
                "MPP import {ImportId} connecting to {Environment} ({Cloud}) for project {TargetProjectId}. " +
                "CorrelationId: {CorrelationId}.",
                input.ImportId,
                environment?.EnvironmentUrl ?? "(legacy: configured environment)",
                environment?.CloudName ?? "legacy",
                input.TargetProjectId,
                environment?.CorrelationId ?? "(none)");

            serviceClient = connectionFactory.CreateClient(environment);
            IOrganizationService service = serviceClient;
            helper = new Helper(service, connectionFactory.ResolveUrl(environment));

            localFilePath = await blobStorage.DownloadToTemporaryFileAsync(input.ContainerName, input.BlobName, cancellationToken);

            MppPlan plan = mppReader.Read(localFilePath);

            MppImportValidator.Validate(plan);

            // Every column this import reads or writes beyond the core lookups is checked against
            // the environment first: Project Operations and older Project Service environments do
            // not share one schema, and a single missing column fails the whole request.
            DataverseEntitySchema projectSchema = new DataverseEntitySchema(service, "msdyn_project");
            DataverseEntitySchema taskSchema = new DataverseEntitySchema(service, "msdyn_projecttask");
            DataverseEntitySchema teamSchema = new DataverseEntitySchema(service, "msdyn_projectteam");
            DataverseEntitySchema assignmentSchema = new DataverseEntitySchema(service, "msdyn_resourceassignment");
            DataverseEntitySchema dependencySchema = new DataverseEntitySchema(service, "msdyn_projecttaskdependency");

            // The project finish is msdyn_finish in Project Operations; older Project Service
            // environments have msdyn_scheduledend instead, and no msdyn_finish at all.
            string? projectStartColumn = DataverseColumns.FirstPresent(projectSchema.Has, "msdyn_scheduledstart");
            string? projectFinishColumn = DataverseColumns.FirstPresent(projectSchema.Has, "msdyn_finish", "msdyn_scheduledend");

            if (projectFinishColumn != "msdyn_finish")
            {
                logger.LogInformation(
                    "MPP import {ImportId}: the project table has no msdyn_finish, so the project finish comes from {FinishColumn}.",
                    input.ImportId,
                    projectFinishColumn ?? "(no finish column)");
            }

            Entity targetProject = service.Retrieve(
                "msdyn_project",
                input.TargetProjectId,
                new ColumnSet(DataverseColumns.Present(projectSchema.Has, "msdyn_subject", projectStartColumn, projectFinishColumn)));

            EntityReference targetProjectReference = targetProject.ToEntityReference();
            DateTime? targetProjectStart = projectStartColumn == null ? null : targetProject.GetAttributeValue<DateTime?>(projectStartColumn);
            DateTime? targetProjectFinish = projectFinishColumn == null ? null : targetProject.GetAttributeValue<DateTime?>(projectFinishColumn);
            BusinessApplication businessApplication = new PoConfiguration(service).getBusinessApplication(targetProject);

            ExistingSchedule existing = RetrieveExistingSchedule(
                service,
                input.TargetProjectId,
                businessApplication,
                taskSchema,
                teamSchema,
                assignmentSchema);

            // PSA tasks carry no bucket, and older Project Service environments have no bucket table.
            EntityReference? bucketReference = businessApplication == BusinessApplication.Psa
                ? null
                : GetOrCreateProjectBucket(service, targetProjectReference);

            BatchWriter writer = new BatchWriter(service);
            ImportCounters counters = new ImportCounters();

            Dictionary<int, Guid> taskIds = ImportTasks(
                service,
                plan,
                existing,
                targetProjectReference,
                bucketReference,
                taskSchema,
                targetProjectStart,
                businessApplication,
                writer,
                counters,
                cancellationToken);

            writer.Flush();

            // Without resourceMap the name / e-mail matching runs untouched: older add-ins and users
            // who never pressed "Check resources" get exactly the behavior they had.
            Dictionary<int, ResolvedTeamMember> teamMemberIds = input.ResourceMap == null
                ? ImportTeamMembers(
                    service,
                    plan,
                    existing,
                    targetProjectReference,
                    teamSchema,
                    targetProjectStart,
                    targetProjectFinish,
                    writer,
                    counters,
                    cancellationToken)
                : ImportTeamMembersFromResourceMap(
                    service,
                    plan,
                    existing,
                    input.ResourceMap,
                    targetProjectReference,
                    teamSchema,
                    targetProjectStart,
                    targetProjectFinish,
                    counters,
                    cancellationToken);

            writer.Flush();

            ImportAssignments(
                service,
                plan,
                existing,
                targetProjectReference,
                assignmentSchema,
                taskIds,
                teamMemberIds,
                targetProjectStart,
                targetProjectFinish,
                writer,
                counters,
                cancellationToken);

            writer.Flush();

            ImportDependencies(
                service,
                plan,
                existing,
                targetProjectReference,
                taskIds,
                dependencySchema,
                writer,
                counters,
                cancellationToken);

            writer.Flush();

            // Last on purpose: by now the assignments of a removed task were already deleted, and
            // every link in the .mpp was written, so whatever is left over was removed from the file.
            DeactivateRemovedDependencies(service, plan, existing, taskIds, counters, cancellationToken);
            DeactivateRemovedTasks(service, plan, existing, counters, cancellationToken);

            string projectName = targetProject.GetAttributeValue<string>("msdyn_subject") ?? input.TargetProjectId.ToString();

            logger.LogInformation(
                "MPP import {ImportId} completed for project {TargetProjectId}. Resources resolved by {ResourceMode}. " +
                "Tasks: {TasksCreated} created, {TasksUpdated} updated, {TasksDeactivated} deactivated, " +
                "{TasksReactivated} reactivated. " +
                "Team members: {ResourcesCreated} created, {ResourcesSkipped} skipped. " +
                "Assignments: {AssignmentsCreated} created, {AssignmentsUpdated} updated, {AssignmentsSkipped} skipped, " +
                "{AssignmentsDeleted} deleted. " +
                "Dependencies: {DependenciesCreated} created, {DependenciesUpdated} updated, " +
                "{DependenciesDeactivated} deactivated, {DependenciesReactivated} reactivated, {DependenciesSkipped} skipped. " +
                "CorrelationId: {CorrelationId}.",
                input.ImportId,
                input.TargetProjectId,
                input.ResourceMap == null
                    ? "name / e-mail matching"
                    : $"resourceMap ({input.ResourceMap.Count} decisions)",
                counters.TasksCreated,
                counters.TasksUpdated,
                counters.TasksDeactivated,
                counters.TasksReactivated,
                counters.ResourcesCreated,
                counters.ResourcesSkipped,
                counters.AssignmentsCreated,
                counters.AssignmentsUpdated,
                counters.AssignmentsSkipped,
                counters.AssignmentsDeleted,
                counters.DependenciesCreated,
                counters.DependenciesUpdated,
                counters.DependenciesDeactivated,
                counters.DependenciesReactivated,
                counters.DependenciesSkipped + plan.SkippedExternalDependencies,
                environment?.CorrelationId ?? "(none)");

            return new ImportMppResult
            {
                ImportId = input.ImportId,
                TargetProjectId = input.TargetProjectId,
                FileName = input.OriginalFileName,
                Completed = true,
                TasksCreated = counters.TasksCreated,
                TasksUpdated = counters.TasksUpdated,
                TasksDeactivated = counters.TasksDeactivated,
                TasksReactivated = counters.TasksReactivated,
                DependenciesCreated = counters.DependenciesCreated,
                DependenciesUpdated = counters.DependenciesUpdated,
                DependenciesDeactivated = counters.DependenciesDeactivated,
                DependenciesReactivated = counters.DependenciesReactivated,
                ResourcesCreated = counters.ResourcesCreated,
                ResourcesSkipped = counters.ResourcesSkipped,
                AssignmentsCreated = counters.AssignmentsCreated,
                AssignmentsUpdated = counters.AssignmentsUpdated,
                AssignmentsSkipped = counters.AssignmentsSkipped + plan.SkippedAssignmentsWithoutResource,
                AssignmentsDeleted = counters.AssignmentsDeleted,
                SkippedResources = counters.SkippedResources,
                InactiveTasksImported = plan.Tasks.Count(task => !task.IsActive),
                NamesTruncated = counters.NamesTruncated,
                DependenciesSkipped = counters.DependenciesSkipped + plan.SkippedExternalDependencies,
                Message =
                    $"Imported {counters.TasksCreated:N0} new tasks and updated {counters.TasksUpdated:N0} " +
                    $"existing ones in project '{projectName}'." +
                    (counters.TasksDeactivated > 0
                        ? $" Deactivated {counters.TasksDeactivated:N0} tasks that are no longer in the file."
                        : string.Empty) +
                    (counters.TasksReactivated > 0
                        ? $" Reactivated {counters.TasksReactivated:N0} tasks that are back in the file."
                        : string.Empty) +
                    $" Dependencies: {counters.DependenciesCreated:N0} " +
                    $"created, {counters.DependenciesUpdated:N0} updated" +
                    (counters.DependenciesDeactivated > 0 ? $", {counters.DependenciesDeactivated:N0} deactivated" : string.Empty) +
                    (counters.DependenciesReactivated > 0 ? $", {counters.DependenciesReactivated:N0} reactivated" : string.Empty) +
                    ". Assignments: " +
                    $"{counters.AssignmentsCreated:N0} created, {counters.AssignmentsUpdated:N0} updated."
            };
        }
        catch (Exception ex)
        {
            TryWriteFailureLog(helper, ex);
            throw;
        }
        finally
        {
            serviceClient?.Dispose();
            MppBlobStorage.TryDeleteLocalFile(localFilePath);

            try
            {
                await blobStorage.DeleteIfExistsAsync(input.ContainerName, input.BlobName, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(
                    cleanupException,
                    "The temporary MPP blob {BlobName} could not be deleted.",
                    input.BlobName);
            }
        }
    }

    // ---------------------------------------------------------------- tasks

    /// <summary>
    /// Resolves every plan task to a Dataverse row id, creating the ones that do not exist yet and
    /// updating the ones that do. Identity comes from msdyn_msprojectclientid, never from the
    /// per-request import id: that is what made every publish duplicate the whole plan.
    /// </summary>
    private Dictionary<int, Guid> ImportTasks(
        IOrganizationService service,
        MppPlan plan,
        ExistingSchedule existing,
        EntityReference targetProject,
        EntityReference? bucket,
        DataverseEntitySchema schema,
        DateTime? targetProjectStart,
        BusinessApplication businessApplication,
        BatchWriter writer,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        // Every id is resolved before any row is built, because a task's parent lookup may point
        // at a task that comes later in the file.
        Dictionary<int, Guid> taskIds = new(plan.Tasks.Count);
        HashSet<int> newTasks = new();

        foreach (MppTaskDefinition sourceTask in plan.Tasks)
        {
            string clientId = sourceTask.SourceUniqueId.ToString(CultureInfo.InvariantCulture);

            if (existing.TasksByClientId.TryGetValue(clientId, out Entity? row))
            {
                taskIds[sourceTask.SourceUniqueId] = row.Id;
            }
            else
            {
                taskIds[sourceTask.SourceUniqueId] = Guid.NewGuid();
                newTasks.Add(sourceTask.SourceUniqueId);
            }
        }

        int currentMaximumSequence = businessApplication == BusinessApplication.Psa
            ? existing.MaximumWbsId
            : existing.MaximumDisplaySequence;

        Dictionary<int, int> taskSequences = plan.Tasks
            .OrderBy(task => task.SourceId)
            .ThenBy(task => task.SourceUniqueId)
            .Select((task, index) => new
            {
                task.SourceUniqueId,
                Sequence = checked(currentMaximumSequence + index + 1)
            })
            .ToDictionary(item => item.SourceUniqueId, item => item.Sequence);

        foreach (MppTaskDefinition sourceTask in plan.Tasks
                     .OrderBy(task => task.OutlineLevel)
                     .ThenBy(task => task.SourceId)
                     .ThenBy(task => task.SourceUniqueId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            Guid taskId = taskIds[sourceTask.SourceUniqueId];

            if (newTasks.Contains(sourceTask.SourceUniqueId))
            {
                Entity task = BuildTask(
                    sourceTask,
                    taskIds,
                    targetProject,
                    bucket,
                    schema,
                    targetProjectStart,
                    plan.HoursPerWorkingDay,
                    taskSequences[sourceTask.SourceUniqueId],
                    businessApplication,
                    out bool nameTruncated);

                task.Id = taskId;
                writer.Create(task);
                counters.TasksCreated++;

                if (nameTruncated)
                {
                    counters.NamesTruncated++;
                }

                continue;
            }

            Entity existingTask = existing.TasksByClientId[sourceTask.SourceUniqueId.ToString(CultureInfo.InvariantCulture)];
            RowState taskState = EnsureActive(service, existingTask, $"Task {sourceTask.SourceUniqueId} ({existingTask.Id})");

            if (taskState == RowState.StillInactive)
            {
                continue;
            }

            if (taskState == RowState.Reactivated)
            {
                counters.TasksReactivated++;
            }

            Entity update = BuildTaskUpdate(
                sourceTask,
                taskId,
                taskIds,
                schema,
                targetProjectStart,
                plan.HoursPerWorkingDay,
                taskSequences[sourceTask.SourceUniqueId],
                businessApplication,
                out bool updateNameTruncated);

            writer.Update(update);
            counters.TasksUpdated++;

            if (updateNameTruncated)
            {
                counters.NamesTruncated++;
            }
        }

        return taskIds;
    }

    /// <summary>
    /// Update payload for an existing task. Deliberately narrower than the create payload: only
    /// the columns the team approved as ours. Progress, actuals, cost and sales columns are owned
    /// by Dynamics and are never written back from the .mpp.
    /// </summary>
    private static Entity BuildTaskUpdate(
        MppTaskDefinition source,
        Guid taskId,
        IReadOnlyDictionary<int, Guid> taskIds,
        DataverseEntitySchema schema,
        DateTime? targetProjectStart,
        decimal hoursPerWorkingDay,
        int taskSequence,
        BusinessApplication businessApplication,
        out bool nameTruncated)
    {
        Entity task = new Entity("msdyn_projecttask") { Id = taskId };

        schema.SetString(task, "msdyn_subject", source.Name, out nameTruncated);

        if (source.ParentSourceUniqueId.HasValue &&
            taskIds.TryGetValue(source.ParentSourceUniqueId.Value, out Guid parentId))
        {
            task["msdyn_parenttask"] = new EntityReference("msdyn_projecttask", parentId);
        }

        decimal safeHoursPerDay = hoursPerWorkingDay <= 0m ? 8m : hoursPerWorkingDay;
        decimal durationDays = source.DurationHours / safeHoursPerDay;
        DateTime start = source.Start ?? targetProjectStart ?? DateTime.UtcNow;
        DateTime finish = source.Finish ?? start.AddDays(Convert.ToDouble(durationDays));

        if (source.IsMilestone)
        {
            finish = start;
            durationDays = 0m;
        }

        schema.SetValue(task, "msdyn_scheduledstart", start);
        schema.SetValue(task, "msdyn_scheduledend", finish);
        schema.SetValue(task, "msdyn_duration", Convert.ToDouble(durationDays));
        schema.SetValue(task, "msdyn_effort", source.EffortHours);

        if (businessApplication == BusinessApplication.Psa)
        {
            schema.SetString(task, "msdyn_wbsid", taskSequence.ToString(CultureInfo.InvariantCulture), out _);
        }
        else
        {
            schema.SetValue(task, "msdyn_start", start);
            schema.SetValue(task, "msdyn_finish", finish);
            schema.SetValue(task, "msdyn_displaysequence", taskSequence);
        }

        return task;
    }

    private enum RowState
    {
        Active,
        Reactivated,
        StillInactive
    }

    /// <summary>
    /// Team decision, 2026-09-11: a task or link that is back in the .mpp is reactivated, even if
    /// it was deactivated by hand in Dynamics. Runs on its own, before the row's field update is
    /// queued, so a row that refuses to come back is skipped instead of failing the whole batch.
    /// </summary>
    private RowState EnsureActive(IOrganizationService service, Entity row, string description)
    {
        if (row.GetAttributeValue<OptionSetValue>("statecode")?.Value != 1)
        {
            return RowState.Active;
        }

        try
        {
            service.Update(new Entity(row.LogicalName, row.Id)
            {
                ["statecode"] = new OptionSetValue(0),
                ["statuscode"] = new OptionSetValue(1)
            });

            logger.LogInformation("{Row} was reactivated: it is back in the .mpp.", description);
            return RowState.Reactivated;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "{Row} is back in the .mpp but could not be reactivated, so it is not updated. Cause: {Cause}",
                description,
                ActivityDiagnostics.Flatten(ex));
            return RowState.StillInactive;
        }
    }

    /// <summary>
    /// Team decision, 2026-09-11: a task removed from the .mpp is deactivated in Dataverse, never
    /// deleted. Only rows this import owns (identifier column set) are candidates, and a row that
    /// is already inactive is left alone; EnsureActive brings it back if it returns to the .mpp.
    /// One update per row, each inside its own try, so a row that refuses does not abort the import.
    /// </summary>
    private void DeactivateRemovedTasks(
        IOrganizationService service,
        MppPlan plan,
        ExistingSchedule existing,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        // A plan without tasks would deactivate the whole project. The validator should never let
        // one through, but this is the one step where that mistake would not be obvious.
        if (plan.Tasks.Count == 0)
        {
            logger.LogWarning("The .mpp has no tasks, so no task is deactivated.");
            return;
        }

        HashSet<string> planKeys = plan.Tasks
            .Select(task => task.SourceUniqueId.ToString(CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);

        foreach (Entity task in existing.OwnTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string clientId = (task.GetAttributeValue<string>(ClientIdColumn) ?? string.Empty).Trim();

            if (planKeys.Contains(clientId) ||
                task.GetAttributeValue<OptionSetValue>("statecode")?.Value != 0)
            {
                continue;
            }

            try
            {
                service.Update(new Entity("msdyn_projecttask", task.Id)
                {
                    ["statecode"] = new OptionSetValue(1),
                    ["statuscode"] = new OptionSetValue(2)
                });

                counters.TasksDeactivated++;

                logger.LogInformation(
                    "Task {ClientId} ({TaskId}) was deactivated: it is no longer in the .mpp.",
                    clientId,
                    task.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Task {ClientId} ({TaskId}) is no longer in the .mpp but could not be deactivated. Cause: {Cause}",
                    clientId,
                    task.Id,
                    ActivityDiagnostics.Flatten(ex));
            }
        }
    }

    /// <summary>
    /// Team decision, 2026-09-11: a link between two tasks of this import that is no longer in the
    /// .mpp is deactivated, never deleted, even one that was added by hand in Dynamics. A link with
    /// an end outside the import, a task created by hand, is never touched. One update per row,
    /// each inside its own try.
    /// </summary>
    private void DeactivateRemovedDependencies(
        IOrganizationService service,
        MppPlan plan,
        ExistingSchedule existing,
        IReadOnlyDictionary<int, Guid> taskIds,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        // Same guard as for tasks: a plan without tasks would deactivate every link.
        if (plan.Tasks.Count == 0)
        {
            logger.LogWarning("The .mpp has no tasks, so no dependency is deactivated.");
            return;
        }

        HashSet<Guid> ownTaskIds = existing.OwnTasks.Select(task => task.Id).ToHashSet();
        HashSet<(Guid, Guid)> planPairs = new();

        foreach (MppDependencyDefinition dependency in plan.Dependencies)
        {
            if (taskIds.TryGetValue(dependency.PredecessorSourceUniqueId, out Guid predecessorId) &&
                taskIds.TryGetValue(dependency.SuccessorSourceUniqueId, out Guid successorId))
            {
                planPairs.Add((predecessorId, successorId));
            }
        }

        foreach (Entity dependency in existing.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Guid? predecessorId = dependency.GetAttributeValue<EntityReference>("msdyn_predecessortask")?.Id;
            Guid? successorId = dependency.GetAttributeValue<EntityReference>("msdyn_successortask")?.Id;

            if (predecessorId == null ||
                successorId == null ||
                !ownTaskIds.Contains(predecessorId.Value) ||
                !ownTaskIds.Contains(successorId.Value) ||
                planPairs.Contains((predecessorId.Value, successorId.Value)) ||
                dependency.GetAttributeValue<OptionSetValue>("statecode")?.Value != 0)
            {
                continue;
            }

            try
            {
                service.Update(new Entity("msdyn_projecttaskdependency", dependency.Id)
                {
                    ["statecode"] = new OptionSetValue(1),
                    ["statuscode"] = new OptionSetValue(2)
                });

                counters.DependenciesDeactivated++;

                logger.LogInformation(
                    "Dependency {DependencyId} was deactivated: it is no longer in the .mpp.",
                    dependency.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Dependency {DependencyId} is no longer in the .mpp but could not be deactivated. Cause: {Cause}",
                    dependency.Id,
                    ActivityDiagnostics.Flatten(ex));
            }
        }
    }

    // ------------------------------------------------------- team members

    /// <summary>
    /// Resolves every .mpp resource to a project team member. Never creates a bookableresource:
    /// when no existing one matches, the resource is skipped and logged. A team member that
    /// already exists but was not created by this import is reused as-is and never written to.
    /// </summary>
    private Dictionary<int, ResolvedTeamMember> ImportTeamMembers(
        IOrganizationService service,
        MppPlan plan,
        ExistingSchedule existing,
        EntityReference targetProject,
        DataverseEntitySchema teamSchema,
        DateTime? targetProjectStart,
        DateTime? targetProjectFinish,
        BatchWriter writer,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        Dictionary<int, ResolvedTeamMember> teamMembers = new();

        if (plan.Resources.Count == 0)
        {
            return teamMembers;
        }

        List<BookableResourceRow> bookableResources;

        try
        {
            bookableResources = RetrieveBookableResources(service);
        }
        catch (Exception ex)
        {
            // One phase failing must not abort the import: tasks and dependencies still go through.
            counters.ResourcesSkipped += plan.Resources.Count;
            logger.LogWarning(
                ex,
                "Bookable resources could not be read, so no team member is resolved and every assignment " +
                "is skipped. Cause: {Cause}",
                ActivityDiagnostics.Flatten(ex));

            return teamMembers;
        }

        foreach (MppResourceDefinition resource in plan.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string clientId = resource.SourceUniqueId.ToString(CultureInfo.InvariantCulture);

                if (existing.TeamMembersByClientId.TryGetValue(clientId, out Entity? owned))
                {
                    teamMembers[resource.SourceUniqueId] = new ResolvedTeamMember(
                        owned.ToEntityReference(),
                        owned.GetAttributeValue<EntityReference>("msdyn_bookableresourceid"));
                    continue;
                }

                BookableResourceRow? matched = MatchBookableResource(resource, bookableResources);

                if (matched == null)
                {
                    counters.ResourcesSkipped++;
                    logger.LogWarning(
                        "Resource '{Resource}' (unique id {UniqueId}) has no matching bookable resource by " +
                        "e-mail or exact name; its assignments are skipped. No bookable resource was created.",
                        resource.Name,
                        resource.SourceUniqueId);
                    continue;
                }

                // A team member already on the project for that resource is reused untouched: it
                // may have been created by hand, and rows we do not own are never written to.
                if (existing.TeamMembersByResource.TryGetValue(matched.Id, out Entity? foreignMember))
                {
                    teamMembers[resource.SourceUniqueId] = new ResolvedTeamMember(
                        foreignMember.ToEntityReference(),
                        new EntityReference("bookableresource", matched.Id));
                    logger.LogInformation(
                        "Resource '{Resource}' reuses the existing project team member {TeamMemberId}, which this " +
                        "import did not create and does not modify.",
                        resource.Name,
                        foreignMember.Id);
                    continue;
                }

                if (matched.CalendarId == null || matched.ResourceCategoryId == null)
                {
                    counters.ResourcesSkipped++;
                    logger.LogWarning(
                        "Bookable resource '{Resource}' cannot be added to the project team: " +
                        "calendar={HasCalendar}, resource category={HasCategory}. Both are required by " +
                        "msdyn_projectteam. Its assignments are skipped.",
                        resource.Name,
                        matched.CalendarId != null,
                        matched.ResourceCategoryId != null);
                    continue;
                }

                Entity teamMember = new Entity("msdyn_projectteam")
                {
                    Id = Guid.NewGuid(),
                    ["msdyn_project"] = targetProject,
                    ["msdyn_name"] = resource.Name,
                    ["msdyn_bookableresourceid"] = new EntityReference("bookableresource", matched.Id),
                    ["msdyn_resourcecategory"] = new EntityReference("bookableresourcecategory", matched.ResourceCategoryId.Value)
                };

                SetTeamMemberSchedule(teamSchema, teamMember, matched.CalendarId.Value, targetProjectStart, targetProjectFinish, clientId);

                writer.Create(teamMember);
                counters.ResourcesCreated++;
                teamMembers[resource.SourceUniqueId] = new ResolvedTeamMember(
                    teamMember.ToEntityReference(),
                    new EntityReference("bookableresource", matched.Id));
            }
            catch (Exception ex)
            {
                counters.ResourcesSkipped++;
                logger.LogWarning(
                    ex,
                    "Resource '{Resource}' (unique id {UniqueId}) could not be added to the project team. " +
                    "Cause: {Cause}",
                    resource.Name,
                    resource.SourceUniqueId,
                    ActivityDiagnostics.Flatten(ex));
            }
        }

        return teamMembers;
    }

    // ------------------------------------------------------- resourceMap

    /// <summary>
    /// The add-in sent the user's decisions in resourceMap. Each listed resource uses the chosen
    /// bookable resource, with no name or e-mail matching at all. A resource the user left out is
    /// skipped: the user saw the choice and left it open, often because the name is ambiguous.
    /// The Function never creates contacts, bookable resources or users. Team members are created
    /// one by one, each inside its own try, so a rejected row skips only its own resource.
    /// </summary>
    private Dictionary<int, ResolvedTeamMember> ImportTeamMembersFromResourceMap(
        IOrganizationService service,
        MppPlan plan,
        ExistingSchedule existing,
        IReadOnlyList<ResourceMapEntry> resourceMap,
        EntityReference targetProject,
        DataverseEntitySchema teamSchema,
        DateTime? targetProjectStart,
        DateTime? targetProjectFinish,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        Dictionary<int, ResolvedTeamMember> teamMembers = new();
        Dictionary<int, ResourceMapEntry> decisions = resourceMap.ToDictionary(entry => entry.ProjectResourceUniqueId);

        logger.LogInformation(
            "resourceMap received with {DecisionCount} decisions for the {ResourceCount} resources of the .mpp. " +
            "Resources are resolved only through it: no name or e-mail matching.",
            decisions.Count,
            plan.Resources.Count);

        HashSet<int> planResourceIds = plan.Resources.Select(resource => resource.SourceUniqueId).ToHashSet();

        foreach (ResourceMapEntry orphan in decisions.Values.Where(entry => !planResourceIds.Contains(entry.ProjectResourceUniqueId)))
        {
            logger.LogWarning(
                "resourceMap lists projectResourceUniqueId {UniqueId} ({Name}), which is not a work resource of " +
                "the .mpp; the entry is ignored.",
                orphan.ProjectResourceUniqueId,
                orphan.ProjectResourceName ?? "no name");
        }

        if (plan.Resources.Count == 0)
        {
            return teamMembers;
        }

        Dictionary<Guid, ChosenResourceRow> chosen;
        HashSet<Guid> activeRoles;

        try
        {
            chosen = RetrieveChosenResources(service, decisions.Values.Select(entry => entry.BookableResourceId));
            activeRoles = RetrieveActiveRoles(
                service,
                decisions.Values
                    .Where(entry => entry.BookableResourceCategoryId.HasValue)
                    .Select(entry => entry.BookableResourceCategoryId!.Value));
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "The bookable resources chosen in resourceMap could not be read, so every resource is skipped. " +
                "Cause: {Cause}",
                ActivityDiagnostics.Flatten(ex));

            foreach (MppResourceDefinition resource in plan.Resources)
            {
                Skip(resource, $"the chosen bookable resources could not be read: {ActivityDiagnostics.Flatten(ex)}");
            }

            return teamMembers;
        }

        foreach (MppResourceDefinition resource in plan.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!decisions.TryGetValue(resource.SourceUniqueId, out ResourceMapEntry? decision))
                {
                    Skip(resource, "it is not in resourceMap; the user left the choice open, so it is not matched by name");
                    continue;
                }

                if (!chosen.TryGetValue(decision.BookableResourceId, out ChosenResourceRow? row))
                {
                    Skip(resource, $"its bookable resource {decision.BookableResourceId} does not exist in this environment");
                    continue;
                }

                if (!row.IsActive)
                {
                    Skip(resource, $"its bookable resource {row.Name} ({row.Id}) is inactive");
                    continue;
                }

                EntityReference bookableResource = new EntityReference("bookableresource", row.Id);

                // Approved 2026-09-10: with resourceMap the team member is resolved by the chosen
                // bookable resource, never by the identifier column, so a stale binding left by an
                // earlier name match cannot win. Existing team members are reused, never modified.
                Entity? existingMember = FindTeamMember(existing, row.Id, resource.SourceUniqueId);

                if (existingMember != null)
                {
                    teamMembers[resource.SourceUniqueId] = new ResolvedTeamMember(existingMember.ToEntityReference(), bookableResource, row.Name);

                    logger.LogInformation(
                        "Resource {Resource} (unique id {UniqueId}) uses bookable resource {BookableResource} from " +
                        "resourceMap and reuses project team member {TeamMemberId} without modifying it.{RoleNote}",
                        resource.Name,
                        resource.SourceUniqueId,
                        row.Name,
                        existingMember.Id,
                        decision.BookableResourceCategoryId.HasValue
                            ? " The role in resourceMap only applies when a team member is created."
                            : string.Empty);
                    continue;
                }

                Guid? roleId = decision.BookableResourceCategoryId ?? row.ResourceCategoryId;

                if (roleId == null)
                {
                    Skip(resource, $"no role: resourceMap carries no bookableResourceCategoryId and bookable resource {row.Name} has no category");
                    continue;
                }

                if (decision.BookableResourceCategoryId.HasValue &&
                    !activeRoles.Contains(decision.BookableResourceCategoryId.Value))
                {
                    Skip(resource, $"its role {decision.BookableResourceCategoryId.Value} does not exist or is inactive");
                    continue;
                }

                if (row.CalendarId == null)
                {
                    Skip(resource, $"bookable resource {row.Name} has no calendar");
                    continue;
                }

                Entity teamMember = new Entity("msdyn_projectteam")
                {
                    ["msdyn_project"] = targetProject,
                    // The chosen resource name, not the .mpp one: with resourceMap they can differ,
                    // and a team member named after someone else would be misleading forever,
                    // because the import never modifies team members once created.
                    ["msdyn_name"] = row.Name,
                    ["msdyn_bookableresourceid"] = bookableResource,
                    ["msdyn_resourcecategory"] = new EntityReference("bookableresourcecategory", roleId.Value)
                };

                SetTeamMemberSchedule(
                    teamSchema,
                    teamMember,
                    row.CalendarId.Value,
                    targetProjectStart,
                    targetProjectFinish,
                    resource.SourceUniqueId.ToString(CultureInfo.InvariantCulture));

                teamMember.Id = service.Create(teamMember);
                counters.ResourcesCreated++;

                teamMembers[resource.SourceUniqueId] = new ResolvedTeamMember(teamMember.ToEntityReference(), bookableResource, row.Name);

                logger.LogInformation(
                    "Resource {Resource} (unique id {UniqueId}) uses bookable resource {BookableResource} from " +
                    "resourceMap; project team member {TeamMemberId} created with role {RoleId} ({RoleSource}).",
                    resource.Name,
                    resource.SourceUniqueId,
                    row.Name,
                    teamMember.Id,
                    roleId.Value,
                    decision.BookableResourceCategoryId.HasValue ? "from resourceMap" : "from the bookable resource");
            }
            catch (Exception ex)
            {
                Skip(resource, $"the project team member could not be prepared: {ActivityDiagnostics.Flatten(ex)}", ex);
            }
        }

        return teamMembers;

        void Skip(MppResourceDefinition resource, string reason, Exception? exception = null)
        {
            counters.ResourcesSkipped++;
            counters.SkippedResources.Add($"{resource.Name} (unique id {resource.SourceUniqueId}): {reason}");

            logger.LogWarning(
                exception,
                "Resource {Resource} (unique id {UniqueId}) skipped: {Reason}. Its assignments are skipped.",
                resource.Name,
                resource.SourceUniqueId,
                reason);
        }
    }

    /// <summary>
    /// The columns of a new team member that not every environment has: calendar, dates and the
    /// identifier. Each one is written only where the table has it, so an older Project Service
    /// environment gets a team member instead of a rejected request.
    /// </summary>
    private static void SetTeamMemberSchedule(
        DataverseEntitySchema schema,
        Entity teamMember,
        Guid calendarId,
        DateTime? projectStart,
        DateTime? projectFinish,
        string clientId)
    {
        DateTime start = projectStart ?? DateTime.UtcNow;

        schema.SetString(teamMember, "msdyn_calendarid", calendarId.ToString(), out _);
        schema.SetValue(teamMember, "msdyn_start", start);
        schema.SetValue(teamMember, "msdyn_finish", projectFinish ?? start);
        schema.SetString(teamMember, ClientIdColumn, clientId, out _);
    }

    /// <summary>
    /// A team member of the project for the chosen bookable resource: the one this import created
    /// for the same .mpp resource if there is one, then any of ours, then the oldest foreign one.
    /// Lists come ordered oldest first, so "last" means the newest.
    /// </summary>
    private static Entity? FindTeamMember(ExistingSchedule existing, Guid bookableResourceId, int resourceUniqueId)
    {
        if (!existing.TeamMemberListsByResource.TryGetValue(bookableResourceId, out List<Entity>? members) ||
            members.Count == 0)
        {
            return null;
        }

        string clientId = resourceUniqueId.ToString(CultureInfo.InvariantCulture);

        return members.LastOrDefault(member =>
                   string.Equals(member.GetAttributeValue<string>(ClientIdColumn), clientId, StringComparison.Ordinal))
               ?? members.LastOrDefault(member =>
                   !string.IsNullOrWhiteSpace(member.GetAttributeValue<string>(ClientIdColumn)))
               ?? members[0];
    }

    private static Dictionary<Guid, ChosenResourceRow> RetrieveChosenResources(IOrganizationService service, IEnumerable<Guid> ids)
    {
        Guid[] distinct = ids.Distinct().ToArray();
        Dictionary<Guid, ChosenResourceRow> rows = new();

        if (distinct.Length == 0)
        {
            return rows;
        }

        QueryExpression query = new QueryExpression("bookableresource")
        {
            ColumnSet = new ColumnSet("name", "statecode", "calendarid"),
            NoLock = true
        };

        query.Criteria.AddCondition("bookableresourceid", ConditionOperator.In, distinct.Cast<object>().ToArray());

        LinkEntity category = query.AddLink("bookableresourcecategoryassn", "bookableresourceid", "resource", JoinOperator.LeftOuter);
        category.EntityAlias = "resourcecategory";
        category.Columns = new ColumnSet("resourcecategory");

        foreach (Entity entity in QueryHelper.RetrieveAll(service, query))
        {
            if (rows.ContainsKey(entity.Id))
            {
                continue;
            }

            rows[entity.Id] = new ChosenResourceRow(
                entity.Id,
                entity.GetAttributeValue<string>("name") ?? entity.Id.ToString(),
                entity.GetAttributeValue<OptionSetValue>("statecode")?.Value == 0,
                entity.GetAttributeValue<EntityReference>("calendarid")?.Id,
                GetAliasedValue<EntityReference>(entity, "resourcecategory.resourcecategory")?.Id);
        }

        return rows;
    }

    private static HashSet<Guid> RetrieveActiveRoles(IOrganizationService service, IEnumerable<Guid> ids)
    {
        Guid[] distinct = ids.Distinct().ToArray();
        HashSet<Guid> active = new();

        if (distinct.Length == 0)
        {
            return active;
        }

        QueryExpression query = new QueryExpression("bookableresourcecategory")
        {
            ColumnSet = new ColumnSet(false),
            NoLock = true
        };

        query.Criteria.AddCondition("bookableresourcecategoryid", ConditionOperator.In, distinct.Cast<object>().ToArray());
        query.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

        foreach (Entity entity in QueryHelper.RetrieveAll(service, query))
        {
            active.Add(entity.Id);
        }

        return active;
    }

    private sealed record ChosenResourceRow(
        Guid Id,
        string Name,
        bool IsActive,
        Guid? CalendarId,
        Guid? ResourceCategoryId);

    // -------------------------------------------------------- assignments

    private void ImportAssignments(
        IOrganizationService service,
        MppPlan plan,
        ExistingSchedule existing,
        EntityReference targetProject,
        DataverseEntitySchema assignmentSchema,
        IReadOnlyDictionary<int, Guid> taskIds,
        IReadOnlyDictionary<int, ResolvedTeamMember> teamMembers,
        DateTime? targetProjectStart,
        DateTime? targetProjectFinish,
        BatchWriter writer,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        // Keys present in the .mpp, whether or not they could be imported. A resource that failed
        // to match still keeps its assignment: only a row the user actually unassigned is deleted.
        HashSet<string> planKeys = plan.Assignments
            .Select(item => string.Create(
                CultureInfo.InvariantCulture,
                $"{item.TaskSourceUniqueId}|{item.ResourceSourceUniqueId}"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (MppAssignmentDefinition assignment in plan.Assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!taskIds.TryGetValue(assignment.TaskSourceUniqueId, out Guid taskId) ||
                    !teamMembers.TryGetValue(assignment.ResourceSourceUniqueId, out ResolvedTeamMember? teamMember))
                {
                    counters.AssignmentsSkipped++;
                    continue;
                }

                string clientId = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{assignment.TaskSourceUniqueId}|{assignment.ResourceSourceUniqueId}");

                DateTime start = assignment.Start ?? targetProjectStart ?? DateTime.UtcNow;
                DateTime finish = assignment.Finish ?? targetProjectFinish ?? start;

                if (existing.AssignmentsByClientId.TryGetValue(clientId, out Entity? owned))
                {
                    Entity update = new Entity("msdyn_resourceassignment")
                    {
                        Id = owned.Id,
                        ["msdyn_projectteamid"] = teamMember.TeamMember
                    };

                    SetAssignmentSchedule(assignmentSchema, update, start, finish, assignment.WorkHours);

                    // resourceMap path only: a repointed assignment takes the name of the resource the
                    // user chose. Without the map DisplayName is null and the name is left as it was.
                    if (teamMember.DisplayName != null)
                    {
                        update["msdyn_name"] = BuildAssignmentName(plan, assignment, teamMember.DisplayName);
                    }

                    // Without the bookable resource the Resource Assignments grid shows the row
                    // under "Unassigned", even though the team member is set.
                    assignmentSchema.SetValue(update, "msdyn_bookableresourceid", teamMember.BookableResource);

                    writer.Update(update);
                    counters.AssignmentsUpdated++;
                    continue;
                }

                Entity row = new Entity("msdyn_resourceassignment")
                {
                    Id = Guid.NewGuid(),
                    ["msdyn_name"] = BuildAssignmentName(plan, assignment, teamMember.DisplayName),
                    ["msdyn_projectid"] = targetProject,
                    ["msdyn_taskid"] = new EntityReference("msdyn_projecttask", taskId),
                    ["msdyn_projectteamid"] = teamMember.TeamMember
                };

                SetAssignmentSchedule(assignmentSchema, row, start, finish, assignment.WorkHours);
                assignmentSchema.SetString(row, ClientIdColumn, clientId, out _);
                assignmentSchema.SetValue(row, "msdyn_bookableresourceid", teamMember.BookableResource);

                writer.Create(row);
                counters.AssignmentsCreated++;
            }
            catch (Exception ex)
            {
                counters.AssignmentsSkipped++;
                logger.LogWarning(
                    ex,
                    "The assignment of resource {ResourceUniqueId} to task {TaskUniqueId} was skipped. Cause: {Cause}",
                    assignment.ResourceSourceUniqueId,
                    assignment.TaskSourceUniqueId,
                    ActivityDiagnostics.Flatten(ex));
            }
        }

        DeleteUnassignedAssignments(service, existing, planKeys, counters, cancellationToken);
    }

    /// <summary>
    /// Dates and effort of an assignment, written only where the table has those columns, and
    /// converted to each column's own type.
    /// </summary>
    private static void SetAssignmentSchedule(
        DataverseEntitySchema schema,
        Entity assignment,
        DateTime start,
        DateTime finish,
        decimal workHours)
    {
        schema.SetValue(assignment, "msdyn_start", start);
        schema.SetValue(assignment, "msdyn_finish", finish);
        schema.SetValue(assignment, "msdyn_effort", workHours);
    }

    /// <summary>
    /// Team decision, 2026-09-09: an assignment removed from the .mpp is deleted in Dataverse.
    /// Scoped to assignments only; tasks and dependencies are still never deleted. Rows whose
    /// identifier column is empty are not in the index and can never reach this method.
    /// Deletes go one by one rather than through the batch, so a row that refuses to be deleted
    /// does not abort the import.
    /// </summary>
    private void DeleteUnassignedAssignments(
        IOrganizationService service,
        ExistingSchedule existing,
        HashSet<string> planKeys,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<string, Entity> owned in existing.AssignmentsByClientId)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (planKeys.Contains(owned.Key))
            {
                continue;
            }

            try
            {
                service.Delete("msdyn_resourceassignment", owned.Value.Id);
                counters.AssignmentsDeleted++;

                logger.LogInformation(
                    "Assignment {ClientId} was deleted: it is no longer in the .mpp.",
                    owned.Key);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Assignment {ClientId} is no longer in the .mpp but could not be deleted. Cause: {Cause}",
                    owned.Key,
                    ActivityDiagnostics.Flatten(ex));
            }
        }
    }

    private static string BuildAssignmentName(MppPlan plan, MppAssignmentDefinition assignment, string? resourceDisplayName = null)
    {
        string taskName = plan.Tasks
            .FirstOrDefault(task => task.SourceUniqueId == assignment.TaskSourceUniqueId)?.Name ?? "Task";

        string resourceName = resourceDisplayName
            ?? plan.Resources
                .FirstOrDefault(resource => resource.SourceUniqueId == assignment.ResourceSourceUniqueId)?.Name
            ?? "Resource";

        string name = $"{taskName} - {resourceName}";

        return name.Length > 100 ? name[..100] : name;
    }

    // -------------------------------------------------------- dependencies

    /// <summary>
    /// Dependencies are last, once every task exists so both ends resolve. There is no
    /// msdyn_msprojectclientid on msdyn_projecttaskdependency, so identity is the natural key:
    /// the predecessor and successor pair inside the project.
    /// </summary>
    private void ImportDependencies(
        IOrganizationService service,
        MppPlan plan,
        ExistingSchedule existing,
        EntityReference targetProject,
        IReadOnlyDictionary<int, Guid> taskIds,
        DataverseEntitySchema schema,
        BatchWriter writer,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        foreach (MppDependencyDefinition dependency in plan.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!taskIds.TryGetValue(dependency.PredecessorSourceUniqueId, out Guid predecessorId) ||
                    !taskIds.TryGetValue(dependency.SuccessorSourceUniqueId, out Guid successorId))
                {
                    counters.DependenciesSkipped++;
                    continue;
                }

                int linkType = MapLinkType(dependency.Type);
                int lagSeconds = ToLagSeconds(dependency.LagHours);

                if (existing.DependenciesByEnds.TryGetValue((predecessorId, successorId), out Entity? owned))
                {
                    RowState dependencyState = EnsureActive(
                        service,
                        owned,
                        $"Dependency {dependency.PredecessorSourceUniqueId} -> {dependency.SuccessorSourceUniqueId} ({owned.Id})");

                    if (dependencyState == RowState.StillInactive)
                    {
                        counters.DependenciesSkipped++;
                        continue;
                    }

                    if (dependencyState == RowState.Reactivated)
                    {
                        counters.DependenciesReactivated++;
                    }

                    Entity update = new Entity("msdyn_projecttaskdependency") { Id = owned.Id };
                    schema.SetValue(update, "msdyn_linktype", linkType);
                    schema.SetValue(update, "msdyn_projecttaskdependencylinklag", lagSeconds);

                    writer.Update(update);
                    counters.DependenciesUpdated++;
                    continue;
                }

                Entity row = BuildDependency(
                    dependency,
                    predecessorId,
                    successorId,
                    targetProject,
                    schema,
                    linkType,
                    lagSeconds);

                row.Id = Guid.NewGuid();
                writer.Create(row);
                counters.DependenciesCreated++;
            }
            catch (Exception ex)
            {
                counters.DependenciesSkipped++;
                logger.LogWarning(
                    ex,
                    "The dependency {Predecessor} -> {Successor} was skipped. Cause: {Cause}",
                    dependency.PredecessorSourceUniqueId,
                    dependency.SuccessorSourceUniqueId,
                    ActivityDiagnostics.Flatten(ex));
            }
        }
    }

    private static int MapLinkType(MppDependencyType type) => type switch
    {
        MppDependencyType.StartToStart => StartToStart,
        MppDependencyType.FinishToFinish => FinishToFinish,
        MppDependencyType.StartToFinish => StartToFinish,
        _ => FinishToStart
    };

    /// <summary>
    /// msdyn_projecttaskdependencylinklag is an integer of SECONDS, per its own metadata
    /// description. The column the previous code wrote, msdyn_lag, does not exist on the table,
    /// so every lag was being dropped silently.
    /// </summary>
    private static int ToLagSeconds(decimal lagHours)
    {
        decimal seconds = lagHours * 3600m;

        if (seconds > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (seconds < int.MinValue)
        {
            return int.MinValue;
        }

        return (int)decimal.Round(seconds, 0, MidpointRounding.AwayFromZero);
    }

    // ------------------------------------------------------------ builders

    private static Entity BuildTask(MppTaskDefinition source, IReadOnlyDictionary<int, Guid> taskIds, EntityReference targetProject, EntityReference? bucket, DataverseEntitySchema schema, DateTime? targetProjectStart, decimal hoursPerWorkingDay, int taskSequence, BusinessApplication businessApplication, out bool nameTruncated)
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
        schema.SetValue(task, "msdyn_effort", source.EffortHours);
        schema.SetValue(task, "msdyn_iscritical", source.IsCritical);
        schema.SetValue(task, "msdyn_progress", source.PercentageComplete);

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
            task["msdyn_projectbucket"] = bucket ?? throw new InvalidOperationException("A Project Operations task needs a project bucket.");
            schema.SetValue(task, "msdyn_start", start);
            schema.SetValue(task, "msdyn_finish", finish);
            schema.SetValue(task, "msdyn_displaysequence", taskSequence);
            schema.SetValue(task, "msdyn_outlinelevel", source.OutlineLevel);
            schema.SetValue(task, "msdyn_summary", source.IsSummary);
        }

        // The half that is invisible in a code review: without this stamp the next publish cannot
        // find the row and creates a duplicate instead.
        if (!schema.SetString(task, ClientIdColumn, source.SourceUniqueId.ToString(CultureInfo.InvariantCulture), out _))
        {
            throw new InvalidOperationException(
                $"The {ClientIdColumn} task field is not available for create operations, so the import " +
                "cannot correlate rows on the next publish and would duplicate them.");
        }

        schema.SetString(task, "msdyn_description", source.Notes, out _);

        return task;
    }

    private static Entity BuildDependency(
        MppDependencyDefinition source,
        Guid predecessorId,
        Guid successorId,
        EntityReference targetProject,
        DataverseEntitySchema schema,
        int linkType,
        int lagSeconds)
    {
        Entity dependency = new Entity("msdyn_projecttaskdependency")
        {
            ["msdyn_project"] = targetProject,
            ["msdyn_predecessortask"] = new EntityReference("msdyn_projecttask", predecessorId),
            ["msdyn_successortask"] = new EntityReference("msdyn_projecttask", successorId)
        };

        schema.SetValue(dependency, "msdyn_linktype", linkType);
        schema.SetValue(dependency, "msdyn_projecttaskdependencylinklag", lagSeconds);

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

    // ------------------------------------------------------------ existing

    private static ExistingSchedule RetrieveExistingSchedule(
        IOrganizationService service,
        Guid targetProjectId,
        BusinessApplication businessApplication,
        DataverseEntitySchema taskSchema,
        DataverseEntitySchema teamSchema,
        DataverseEntitySchema assignmentSchema)
    {
        // Only columns the environment has: a table without the identifier column simply yields
        // no owned rows, instead of failing the whole read.
        QueryExpression taskQuery = new QueryExpression("msdyn_projecttask")
        {
            ColumnSet = new ColumnSet(DataverseColumns.Present(
                taskSchema.Has,
                businessApplication == BusinessApplication.Psa ? "msdyn_wbsid" : "msdyn_displaysequence",
                ClientIdColumn,
                "statecode")),
            NoLock = true
        };

        taskQuery.Criteria.AddCondition("msdyn_project", ConditionOperator.Equal, targetProjectId);

        List<Entity> allTasks = QueryHelper.RetrieveAll(service, taskQuery);

        Dictionary<string, Entity> tasksByClientId = IndexByClientId(allTasks);

        // Every owned row, not only the one per key that the index keeps: when a key is gone from
        // the .mpp, all the rows carrying it are deactivated.
        List<Entity> ownTasks = allTasks
            .Where(row => !string.IsNullOrWhiteSpace(row.GetAttributeValue<string>(ClientIdColumn)))
            .ToList();

        int maximumDisplaySequence = 0;
        int maximumWbsId = 0;

        // Only rows this import does NOT own count towards the maximum. Including our own rows
        // would push the numbering higher on every publish, so the same file would produce a
        // different display sequence each time it is republished.
        foreach (Entity task in allTasks.Where(row => string.IsNullOrWhiteSpace(row.GetAttributeValue<string>(ClientIdColumn))))
        {
            if (businessApplication == BusinessApplication.Psa)
            {
                if (int.TryParse(
                        task.GetAttributeValue<string>("msdyn_wbsid"),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsedWbsId))
                {
                    maximumWbsId = Math.Max(maximumWbsId, parsedWbsId);
                }

                continue;
            }

            if (task.Attributes.TryGetValue("msdyn_displaysequence", out object? value) && value != null)
            {
                maximumDisplaySequence = Math.Max(
                    maximumDisplaySequence,
                    Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }
        }

        QueryExpression teamQuery = new QueryExpression("msdyn_projectteam")
        {
            ColumnSet = new ColumnSet(DataverseColumns.Present(teamSchema.Has, ClientIdColumn, "msdyn_bookableresourceid", "createdon")),
            NoLock = true
        };

        teamQuery.Criteria.AddCondition("msdyn_project", ConditionOperator.Equal, targetProjectId);

        // Oldest first, so wherever several team members share a key the newest one wins, and it
        // does so deterministically: after a resourceMap publish replaced a stale binding, a later
        // publish without the map reuses the user's latest choice.
        teamQuery.AddOrder("createdon", OrderType.Ascending);

        List<Entity> allTeamMembers = QueryHelper.RetrieveAll(service, teamQuery);

        Dictionary<Guid, Entity> teamMembersByResource = new();
        Dictionary<Guid, List<Entity>> teamMemberListsByResource = new();
        foreach (Entity member in allTeamMembers)
        {
            EntityReference? resource = member.GetAttributeValue<EntityReference>("msdyn_bookableresourceid");
            if (resource != null)
            {
                teamMembersByResource[resource.Id] = member;

                if (!teamMemberListsByResource.TryGetValue(resource.Id, out List<Entity>? list))
                {
                    list = new List<Entity>();
                    teamMemberListsByResource[resource.Id] = list;
                }

                list.Add(member);
            }
        }

        QueryExpression assignmentQuery = new QueryExpression("msdyn_resourceassignment")
        {
            ColumnSet = new ColumnSet(DataverseColumns.Present(assignmentSchema.Has, ClientIdColumn)),
            NoLock = true
        };

        assignmentQuery.Criteria.AddCondition("msdyn_projectid", ConditionOperator.Equal, targetProjectId);

        List<Entity> allAssignments = QueryHelper.RetrieveAll(service, assignmentQuery);

        QueryExpression dependencyQuery = new QueryExpression("msdyn_projecttaskdependency")
        {
            ColumnSet = new ColumnSet("msdyn_predecessortask", "msdyn_successortask", "statecode"),
            NoLock = true
        };

        dependencyQuery.Criteria.AddCondition("msdyn_project", ConditionOperator.Equal, targetProjectId);

        List<Entity> allDependencies = QueryHelper.RetrieveAll(service, dependencyQuery);

        Dictionary<(Guid, Guid), Entity> dependenciesByEnds = new();
        foreach (Entity dependency in allDependencies)
        {
            EntityReference? predecessor = dependency.GetAttributeValue<EntityReference>("msdyn_predecessortask");
            EntityReference? successor = dependency.GetAttributeValue<EntityReference>("msdyn_successortask");

            if (predecessor != null && successor != null)
            {
                dependenciesByEnds[(predecessor.Id, successor.Id)] = dependency;
            }
        }

        return new ExistingSchedule(
            maximumDisplaySequence,
            maximumWbsId,
            tasksByClientId,
            ownTasks,
            IndexByClientId(allTeamMembers),
            teamMembersByResource,
            teamMemberListsByResource,
            IndexByClientId(allAssignments),
            dependenciesByEnds,
            allDependencies);
    }

    /// <summary>
    /// Rows whose identifier column is empty were not created by this import: they are excluded
    /// here so nothing downstream can match, update or delete them.
    /// </summary>
    private static Dictionary<string, Entity> IndexByClientId(IEnumerable<Entity> rows)
    {
        Dictionary<string, Entity> index = new(StringComparer.Ordinal);

        foreach (Entity row in rows)
        {
            string? clientId = row.GetAttributeValue<string>(ClientIdColumn);

            if (!string.IsNullOrWhiteSpace(clientId))
            {
                index[clientId.Trim()] = row;
            }
        }

        return index;
    }

    private static List<BookableResourceRow> RetrieveBookableResources(IOrganizationService service)
    {
        QueryExpression query = new QueryExpression("bookableresource")
        {
            ColumnSet = new ColumnSet("name", "calendarid", "userid"),
            NoLock = true
        };

        LinkEntity user = query.AddLink("systemuser", "userid", "systemuserid", JoinOperator.LeftOuter);
        user.EntityAlias = "resourceuser";
        user.Columns = new ColumnSet("internalemailaddress");

        LinkEntity category = query.AddLink("bookableresourcecategoryassn", "bookableresourceid", "resource", JoinOperator.LeftOuter);
        category.EntityAlias = "resourcecategory";
        category.Columns = new ColumnSet("resourcecategory");

        List<BookableResourceRow> rows = new();
        HashSet<Guid> seen = new();

        foreach (Entity entity in QueryHelper.RetrieveAll(service, query))
        {
            if (!seen.Add(entity.Id))
            {
                continue;
            }

            rows.Add(new BookableResourceRow(
                entity.Id,
                entity.GetAttributeValue<string>("name"),
                GetAliasedValue<string>(entity, "resourceuser.internalemailaddress"),
                // calendarid is a Lookup on bookableresource, not a Uniqueidentifier.
                entity.GetAttributeValue<EntityReference>("calendarid")?.Id,
                GetAliasedValue<EntityReference>(entity, "resourcecategory.resourcecategory")?.Id));
        }

        return rows;
    }

    private static T? GetAliasedValue<T>(Entity entity, string key)
    {
        if (entity.Attributes.TryGetValue(key, out object? value) &&
            value is AliasedValue aliased &&
            aliased.Value is T typed)
        {
            return typed;
        }

        return default;
    }

    /// <summary>
    /// Team-approved default: match on the bookable resource's user e-mail, case-insensitive,
    /// falling back to an exact name match. No match means the assignment is skipped.
    /// </summary>
    private static BookableResourceRow? MatchBookableResource(
        MppResourceDefinition resource,
        List<BookableResourceRow> candidates)
    {
        if (!string.IsNullOrWhiteSpace(resource.EmailAddress))
        {
            BookableResourceRow? byEmail = candidates.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Email) &&
                string.Equals(candidate.Email, resource.EmailAddress, StringComparison.OrdinalIgnoreCase));

            if (byEmail != null)
            {
                return byEmail;
            }
        }

        return candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, resource.Name, StringComparison.Ordinal));
    }

    private static void ValidateInput(ImportMppRequest input)
    {
        if (input.ImportId == Guid.Empty)
        {
            throw new ArgumentException("Import ID cannot be empty.", nameof(input));
        }

        if (input.TargetProjectId == Guid.Empty)
        {
            throw new ArgumentException("Target project ID cannot be empty.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.ContainerName) ||
            string.IsNullOrWhiteSpace(input.BlobName))
        {
            throw new ArgumentException("The uploaded MPP file reference is missing.", nameof(input));
        }
    }

    /// <summary>
    /// Writes the failure row without ever masking the original exception. If the connection was
    /// never established, or mfd_pocopyprojectlog is missing or not writable by the application
    /// user, the write is skipped and only the real error propagates.
    /// </summary>
    private void TryWriteFailureLog(Helper? helper, Exception exception)
    {
        if (helper == null)
        {
            logger.LogWarning(
                "The failure could not be written to mfd_pocopyprojectlog: the Dataverse connection " +
                "was never established.");
            return;
        }

        try
        {
            helper.createLog($"{exception.Message}", false, null, null, true);
        }
        catch (Exception logException)
        {
            logger.LogWarning(
                logException,
                "The failure row could not be written to mfd_pocopyprojectlog. The original error is " +
                "the one reported by the activity.");
        }
    }

    private sealed class ImportCounters
    {
        public int TasksCreated { get; set; }

        public int TasksUpdated { get; set; }

        public int TasksDeactivated { get; set; }

        public int TasksReactivated { get; set; }

        public int NamesTruncated { get; set; }

        public int ResourcesCreated { get; set; }

        public int ResourcesSkipped { get; set; }

        public int AssignmentsCreated { get; set; }

        public int AssignmentsUpdated { get; set; }

        public int AssignmentsSkipped { get; set; }

        public int AssignmentsDeleted { get; set; }

        public List<string> SkippedResources { get; } = new();

        public int DependenciesCreated { get; set; }

        public int DependenciesDeactivated { get; set; }

        public int DependenciesReactivated { get; set; }

        public int DependenciesUpdated { get; set; }

        public int DependenciesSkipped { get; set; }
    }

    /// <summary>
    /// A project team member plus the bookable resource behind it. The assignment needs both:
    /// msdyn_projectteamid alone leaves the row under "Unassigned" in the Resource Assignments grid.
    /// </summary>
    private sealed record ResolvedTeamMember(
        EntityReference TeamMember,
        EntityReference? BookableResource,
        string? DisplayName = null);

    private sealed record BookableResourceRow(
        Guid Id,
        string? Name,
        string? Email,
        Guid? CalendarId,
        Guid? ResourceCategoryId);

    private sealed record ExistingSchedule(
        int MaximumDisplaySequence,
        int MaximumWbsId,
        Dictionary<string, Entity> TasksByClientId,
        List<Entity> OwnTasks,
        Dictionary<string, Entity> TeamMembersByClientId,
        Dictionary<Guid, Entity> TeamMembersByResource,
        Dictionary<Guid, List<Entity>> TeamMemberListsByResource,
        Dictionary<string, Entity> AssignmentsByClientId,
        Dictionary<(Guid, Guid), Entity> DependenciesByEnds,
        List<Entity> Dependencies);
}
