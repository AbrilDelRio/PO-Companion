using DC.CopyProyectFromTemplate;
using DC.CopyProyectFromTemplate.Models;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectFromTemplate.Services;

public sealed class CopyProyectFromTemplateService
{
    private readonly DataverseConnectionFactory connectionFactory;
    private readonly ILogger<CopyProyectFromTemplateService> logger;

    public CopyProyectFromTemplateService(DataverseConnectionFactory connectionFactory, ILogger<CopyProyectFromTemplateService> logger)
    {
        this.connectionFactory = connectionFactory;
        this.logger = logger;
    }

    public CopyProjectResult Execute(Guid sourceProjectId, Guid targetProjectId, DataverseEnvironmentTarget? environmentTarget = null)
    {
        if (sourceProjectId == Guid.Empty)
        {
            throw new ArgumentException("Source project ID cannot be empty.", nameof(sourceProjectId));
        }

        if (targetProjectId == Guid.Empty)
        {
            throw new ArgumentException("Target project ID cannot be empty.", nameof(targetProjectId));
        }

        if (sourceProjectId == targetProjectId)
        {
            throw new ArgumentException("Source and target project IDs must be different.", nameof(targetProjectId));
        }

        ResolvedDataverseEnvironment? environment = DataverseConnectionFactory.Resolve(environmentTarget);
        string dataverseUrl = connectionFactory.ResolveUrl(environment);

        using ServiceClient serviceClient = connectionFactory.CreateClient(environment);
        IOrganizationService service = serviceClient;

        Entity target = service.Retrieve("msdyn_project", targetProjectId, new ColumnSet("createdby", "msdyn_subject"));

        EntityReference createdBy = target.GetAttributeValue<EntityReference>("createdby") ?? throw new InvalidPluginExecutionException("The target project does not contain the createdby reference.");

        PoConfiguration poConfiguration = new PoConfiguration(service);
        ProjectTaskClass POTask = new ProjectTaskClass(service, dataverseUrl);
        ProjectClass POProject = new ProjectClass(service, dataverseUrl);
        TeamMemberClass TeamMember = new TeamMemberClass(service, dataverseUrl);
        Helper helper = new Helper(service, dataverseUrl);
        BatchWriter writer = new BatchWriter(service);

        logger.LogInformation(
            "Starting CopyProyectFromTemplate from source project {SourceProjectId} " +
            "to target project {TargetProjectId} on environment {Environment} ({Cloud}). " +
            "CorrelationId: {CorrelationId}.",
            sourceProjectId,
            targetProjectId,
            environment?.Host ?? dataverseUrl,
            environment?.CloudName ?? "legacy",
            environment?.CorrelationId ?? string.Empty);

        try
        {
            string poCodeConfiguration = poConfiguration.getConfigurationCode(target);

            Entity entityPoConfig = poConfiguration.getPOConfiguration(poCodeConfiguration);

            if (entityPoConfig == null)
            {
                return new CopyProjectResult
                {
                    SourceProjectId = sourceProjectId,
                    TargetProjectId = targetProjectId,
                    Completed = false,
                    Message = $"No PO copy configuration was found for code '{poCodeConfiguration}'."
                };
            }

            string templateTableField =
                entityPoConfig.GetAttributeValue<string>("mfd_templatefield")
                ?? string.Empty;

                List<Entity> entityPo = poConfiguration.getProjectFieldsToCopy(entityPoConfig, service);

                Entity targetFull = POProject.CopyProjectFields(
                    entityPo,
                    target,
                    sourceProjectId,
                    templateTableField);

                EntityReference sourceProject = new EntityReference(
                    "msdyn_project",
                    sourceProjectId);

                helper.createLog("Project Copied", true, targetFull.Id, sourceProject.Id);

                int copyTeamMembers = entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copyteammembers")?.Value ?? 0;
                Dictionary<Guid, EntityReference>? teamMemberMap = null;

                if (copyTeamMembers != 0)
                {
                    teamMemberMap = TeamMember.copyTeamMembers(
                        copyTeamMembers,
                        sourceProject,
                        targetFull,
                        copyTeamMembers,
                        writer);
                }

                int copyTasks = entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copytask")?.Value ?? 0;

                if (copyTasks != 0)
                {
                    string[] tasksColumns = poConfiguration.getProjectTaskFieldsToCopy(entityPoConfig);
                    string[] resourceAssignmentsColumns = helper.getTableFields("msdyn_resourceassignment");

                    List<Entity> templateTasks = POTask.getTasks(tasksColumns, sourceProject.Id);
                    helper.createLog("tasks retrived", true, targetFull.Id, sourceProject.Id);

                    Entity projectBucket = POTask.createProjectBucket(targetFull);
                    helper.createLog("project bucket created", true, targetFull.Id, sourceProject.Id);

                    List<Entity> parentTasks;
                    Dictionary<Guid, List<Entity>> childrenByParent =
                        ProjectTaskClass.GroupChildren(templateTasks, out parentTasks);

                    OptionSetValue copyResources =
                        entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copyresources");

                    int copyDatesMethod =
                        entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copydatesmethod")?.Value ?? 0;

                    helper.createLog(
                        $"Copy dates method value: {copyDatesMethod}",
                        true,
                        targetFull.Id,
                        sourceProject.Id);

                    Entity sourceProjectStartDate = service.Retrieve(
                        "msdyn_project",
                        sourceProject.Id,
                        new ColumnSet("msdyn_scheduledstart"));

                    Entity targetProjectStartDate = service.Retrieve(
                        "msdyn_project",
                        targetFull.Id,
                        new ColumnSet("msdyn_scheduledstart"));

                    CopyContext ctx = new CopyContext
                    {
                        TargetProject = targetFull,
                        TargetProjectRef = targetFull.ToEntityReference(),
                        SourceProject = sourceProject,
                        ProjectBucketRef = projectBucket.ToEntityReference(),
                        TaskColumns = tasksColumns,
                        ResourceColumns = resourceAssignmentsColumns,
                        CopyResourcesValue = copyResources?.Value ?? 1,
                        TeamMemberMap = teamMemberMap ?? new Dictionary<Guid, EntityReference>(0),
                        ChildrenByParent = childrenByParent,
                        LabelsByTask = POTask.GetLabelsByTask(sourceProject.Id),
                        Writer = writer,
                        CopyDatesMethod = copyDatesMethod,
                        TargetProjectStart = targetProjectStartDate.GetAttributeValue<DateTime?>("msdyn_scheduledstart"),
                        SourceProjectStart = sourceProjectStartDate.GetAttributeValue<DateTime?>("msdyn_scheduledstart")
                    };

                    if (ctx.CopyResourcesValue == 3)
                    {
                        ctx.GenericResourceIds = TeamMemberClass.GetGenericResourceIds(service);
                    }

                    if (copyTasks == 1)
                    {
                        foreach (Entity task in parentTasks)
                        {
                            POTask.CopyTaskRecursive(task, null, ctx, true);
                        }
                    }
                    else if (copyTasks == 2)
                    {
                        foreach (Entity task in parentTasks)
                        {
                            POTask.CopyTaskRecursive(task, null, ctx, false);
                        }
                    }
                    else if (copyTasks == 3)
                    {
                        foreach (Entity task in templateTasks)
                        {
                            if (task.GetAttributeValue<bool>("msdyn_ismilestone"))
                            {
                                // recurse=false: this foreach already walks every milestone. With true a parent
                                // milestone would copy its children and the foreach would copy them again
                                // (duplicate tasks and an overwritten taskMapping).
                                POTask.CopyTaskRecursive(task, null, ctx, false);
                            }
                        }
                    }

                    writer.Flush();

                    helper.createLog("Copying Dependencies", true, targetFull.Id, sourceProject.Id);
                    POTask.CopyDependencies(sourceProject.Id, targetFull, writer);
                    writer.Flush();

                    if (copyTasks == 1)
                    {
                        OptionSetValue createRequirements =
                            entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_generaterequirements");

                        if (createRequirements != null && createRequirements.Value == 1)
                        {
                        }
                    }
                }

                writer.Flush();
                helper.createLog("Project copied successfully", true, targetFull.Id, sourceProject.Id);
                helper.SendProjectCopiedNotification(service, createdBy.Id, target);

            logger.LogInformation(
                "CopyProyectFromTemplate completed from source project {SourceProjectId} " +
                "to target project {TargetProjectId}.",
                sourceProjectId,
                targetProjectId);

            return new CopyProjectResult
            {
                SourceProjectId = sourceProjectId,
                TargetProjectId = targetProjectId,
                Completed = true,
                Message = "Project copied successfully."
            };
        }
        catch (Exception ex)
        {
            writer.TryFlush();

            try
            {
                helper.SendProjectCopiedNotification(service, createdBy.Id, target, false);
            }
            catch (Exception notificationException)
            {
                logger.LogError(
                    notificationException,
                    "The copy failed and the failure notification could not be sent for project {TargetProjectId}.",
                    targetProjectId);
            }

            logger.LogError(
                ex,
                "CopyProyectFromTemplate failed from source project {SourceProjectId} " +
                "to target project {TargetProjectId}.",
                sourceProjectId,
                targetProjectId);

            throw new InvalidPluginExecutionException($"Error: {ex.Message}", ex);
        }
    }
    /// <summary>
    /// Copies a project from the source environment into a DIFFERENT one. The project is created in
    /// the destination with a new id, and every record copied with it (tasks, buckets, team members,
    /// resource assignments, dependencies, labels) gets a new id too; the links between them are
    /// rebuilt from the new ids. Lookups that point at records outside the project (users, resources,
    /// labels, currencies...) are mapped to their equivalent in the destination, see
    /// <see cref="ReferenceResolver"/>; the ones without equivalent are left out and listed in the
    /// result's warnings. If the copy fails after the project was created, the new project is deleted
    /// so a retry does not leave a half-copied one behind.
    /// </summary>
    public CopyProjectResult ExecuteToEnvironment(Guid sourceProjectId, DataverseEnvironmentTarget? sourceTarget, DataverseEnvironmentTarget destinationTarget)
    {
        if (sourceProjectId == Guid.Empty)
        {
            throw new ArgumentException("Source project ID cannot be empty.", nameof(sourceProjectId));
        }

        ResolvedDataverseEnvironment? sourceEnvironment = DataverseConnectionFactory.Resolve(sourceTarget);
        ResolvedDataverseEnvironment destinationEnvironment = DataverseConnectionFactory.Resolve(destinationTarget)
            ?? throw new ArgumentException("The destination environment is missing.", nameof(destinationTarget));

        if (sourceEnvironment != null &&
            string.Equals(sourceEnvironment.Host, destinationEnvironment.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The source and destination environments must be different.", nameof(destinationTarget));
        }

        string sourceUrl = connectionFactory.ResolveUrl(sourceEnvironment);
        string destinationUrl = destinationEnvironment.EnvironmentUrl;
        string correlationId = destinationEnvironment.CorrelationId;

        using ServiceClient sourceClient = connectionFactory.CreateClient(sourceEnvironment);
        using ServiceClient destinationClient = connectionFactory.CreateClient(destinationEnvironment);
        IOrganizationService sourceService = sourceClient;
        IOrganizationService destinationService = destinationClient;

        // Existence check and the few columns the rest of the flow needs from the source project.
        Entity sourceProjectHeader = sourceService.Retrieve(
            "msdyn_project",
            sourceProjectId,
            new ColumnSet("msdyn_subject", "msdyn_scheduledstart", "createdby", "ownerid"));

        PoConfiguration poConfiguration = new PoConfiguration(sourceService);
        string poCodeConfiguration = poConfiguration.getConfigurationCode(sourceProjectHeader);
        Entity? entityPoConfig = poConfiguration.getPOConfiguration(poCodeConfiguration);

        if (entityPoConfig == null)
        {
            return new CopyProjectResult
            {
                SourceProjectId = sourceProjectId,
                TargetEnvironment = destinationUrl,
                Completed = false,
                Message = $"No PO copy configuration was found for code '{poCodeConfiguration}' in the source environment."
            };
        }

        Helper sourceHelper = new Helper(sourceService, sourceUrl);
        Helper helper = new Helper(destinationService, destinationUrl, sourceUrl);
        ReferenceResolver resolver = new ReferenceResolver(sourceService, destinationService);
        ProjectClass POProject = new ProjectClass(sourceService, destinationService, sourceUrl, destinationUrl);
        ProjectTaskClass POTask = new ProjectTaskClass(sourceService, destinationService, sourceUrl, destinationUrl);
        TeamMemberClass TeamMember = new TeamMemberClass(sourceService, destinationService, sourceUrl, destinationUrl);
        BatchWriter writer = new BatchWriter(destinationService);

        logger.LogInformation(
            "Starting cross-environment copy of project {SourceProjectId} from {SourceEnvironment} to {DestinationEnvironment}. " +
            "CorrelationId: {CorrelationId}.",
            sourceProjectId,
            sourceUrl,
            destinationUrl,
            correlationId);

        Entity? newProject = null;

        try
        {
            string templateTableField =
                entityPoConfig.GetAttributeValue<string>("mfd_templatefield")
                ?? string.Empty;

            List<Entity> entityPo = poConfiguration.getProjectFieldsToCopy(entityPoConfig, sourceService);

            newProject = POProject.CreateProjectFromSource(entityPo, sourceProjectId, templateTableField, resolver);

            EntityReference sourceProject = new EntityReference("msdyn_project", sourceProjectId);

            helper.createLog("Project created in the destination environment", true, newProject.Id, sourceProject.Id);

            int copyTeamMembers = entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copyteammembers")?.Value ?? 0;
            Dictionary<Guid, EntityReference>? teamMemberMap = null;

            if (copyTeamMembers != 0)
            {
                teamMemberMap = TeamMember.copyTeamMembers(
                    copyTeamMembers,
                    sourceProject,
                    newProject,
                    copyTeamMembers,
                    writer,
                    resolver);
            }

            int copyTasks = entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copytask")?.Value ?? 0;

            if (copyTasks != 0)
            {
                string[] tasksColumns = poConfiguration.getProjectTaskFieldsToCopy(entityPoConfig);
                string[] resourceAssignmentsColumns = helper.GetCommonTableFields(sourceHelper, "msdyn_resourceassignment");

                // The tasks are READ with the whole list (the copy logic needs some of those columns) but
                // only the columns the destination accepts are WRITTEN.
                string[] writableTaskColumns = ProjectTaskClass.FilterWritableColumns(
                    tasksColumns, helper.getTableFields("msdyn_projecttask"), resolver, "task");

                List<Entity> templateTasks = POTask.getTasks(tasksColumns, sourceProject.Id);
                helper.createLog("tasks retrived", true, newProject.Id, sourceProject.Id);

                Entity projectBucket = POTask.createProjectBucket(newProject);
                helper.createLog("project bucket created", true, newProject.Id, sourceProject.Id);

                List<Entity> parentTasks;
                Dictionary<Guid, List<Entity>> childrenByParent =
                    ProjectTaskClass.GroupChildren(templateTasks, out parentTasks);

                OptionSetValue? copyResources =
                    entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copyresources");

                int copyDatesMethod =
                    entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copydatesmethod")?.Value ?? 0;

                helper.createLog(
                    $"Copy dates method value: {copyDatesMethod}",
                    true,
                    newProject.Id,
                    sourceProject.Id);

                CopyContext ctx = new CopyContext
                {
                    TargetProject = newProject,
                    TargetProjectRef = newProject.ToEntityReference(),
                    SourceProject = sourceProject,
                    ProjectBucketRef = projectBucket.ToEntityReference(),
                    TaskColumns = writableTaskColumns,
                    ResourceColumns = resourceAssignmentsColumns,
                    CopyResourcesValue = copyResources?.Value ?? 1,
                    TeamMemberMap = teamMemberMap ?? new Dictionary<Guid, EntityReference>(0),
                    ChildrenByParent = childrenByParent,
                    LabelsByTask = POTask.GetLabelsByTask(sourceProject.Id),
                    Writer = writer,
                    CopyDatesMethod = copyDatesMethod,
                    TargetProjectStart = newProject.GetAttributeValue<DateTime?>("msdyn_scheduledstart"),
                    SourceProjectStart = sourceProjectHeader.GetAttributeValue<DateTime?>("msdyn_scheduledstart"),
                    Resolver = resolver
                };

                if (ctx.CopyResourcesValue == 3)
                {
                    ctx.GenericResourceIds = TeamMemberClass.GetGenericResourceIds(sourceService);
                }

                if (copyTasks == 1)
                {
                    foreach (Entity task in parentTasks)
                    {
                        POTask.CopyTaskRecursive(task, null, ctx, true);
                    }
                }
                else if (copyTasks == 2)
                {
                    foreach (Entity task in parentTasks)
                    {
                        POTask.CopyTaskRecursive(task, null, ctx, false);
                    }
                }
                else if (copyTasks == 3)
                {
                    foreach (Entity task in templateTasks)
                    {
                        if (task.GetAttributeValue<bool>("msdyn_ismilestone"))
                        {
                            // recurse=false: this foreach already walks every milestone (see Execute).
                            POTask.CopyTaskRecursive(task, null, ctx, false);
                        }
                    }
                }

                writer.Flush();

                helper.createLog("Copying Dependencies", true, newProject.Id, sourceProject.Id);
                POTask.CopyDependencies(sourceProject.Id, newProject, writer, resolver);
                writer.Flush();
            }

            writer.Flush();

            // The data is all in place from here on. Logging is the last, optional step: if it fails the
            // copy stays, instead of being rolled back for the sake of a log row.
            TryLogWarnings(helper, resolver.Warnings, newProject.Id, sourceProject.Id, correlationId);
            TryLog(helper, "Project copied successfully", newProject.Id, sourceProject.Id, correlationId);
            TrySendNotification(destinationService, destinationUrl, sourceProjectHeader, newProject, resolver, true, correlationId);

            logger.LogInformation(
                "Cross-environment copy completed: source project {SourceProjectId} -> new project {TargetProjectId} on {DestinationEnvironment}. " +
                "{WarningCount} reference(s) left out. CorrelationId: {CorrelationId}.",
                sourceProjectId,
                newProject.Id,
                destinationUrl,
                resolver.Warnings.Count,
                correlationId);

            return new CopyProjectResult
            {
                SourceProjectId = sourceProjectId,
                TargetProjectId = newProject.Id,
                TargetEnvironment = destinationUrl,
                Completed = true,
                Message = resolver.Warnings.Count == 0
                    ? $"Project copied to {destinationEnvironment.Host}."
                    : $"Project copied to {destinationEnvironment.Host}; {resolver.Warnings.Count} reference(s) had no equivalent there and were left out.",
                Warnings = resolver.Warnings
            };
        }
        catch (Exception ex)
        {
            Guid? createdProjectId = newProject?.Id;
            string rollback = string.Empty;

            if (createdProjectId.HasValue)
            {
                try
                {
                    destinationService.Delete("msdyn_project", createdProjectId.Value);
                    rollback = " The partially copied project was deleted from the destination.";
                }
                catch (Exception deleteException)
                {
                    rollback = $" The partially copied project {createdProjectId.Value} could not be deleted from the destination and must be removed manually.";

                    logger.LogError(
                        deleteException,
                        "Rollback of project {TargetProjectId} on {DestinationEnvironment} failed. CorrelationId: {CorrelationId}.",
                        createdProjectId.Value,
                        destinationUrl,
                        correlationId);
                }
            }

            TrySendNotification(destinationService, destinationUrl, sourceProjectHeader, newProject ?? sourceProjectHeader, resolver, false, correlationId);

            logger.LogError(
                ex,
                "Cross-environment copy of project {SourceProjectId} to {DestinationEnvironment} failed. CorrelationId: {CorrelationId}.",
                sourceProjectId,
                destinationUrl,
                correlationId);

            throw new InvalidPluginExecutionException($"Error: {ex.Message}{rollback}", ex);
        }
    }

    /// <summary>
    /// One log row per group of warnings that fits the 2000 characters of the log column, so none is lost.
    /// </summary>
    private void TryLogWarnings(Helper helper, List<string> warnings, Guid projectId, Guid sourceProjectId, string correlationId)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        // 1800, not 2000: the row also carries a prefix of up to ~80 characters.
        List<string> rows = GroupWarnings(warnings, 1800);

        for (int i = 0; i < rows.Count; i++)
        {
            string prefix = rows.Count == 1
                ? $"{warnings.Count} reference(s) without equivalent were left out: "
                : $"References without equivalent were left out ({i + 1}/{rows.Count}, {warnings.Count} in total): ";

            TryLog(helper, prefix + rows[i], projectId, sourceProjectId, correlationId);
        }
    }

    /// <summary>Joins the warnings with "; " into texts of at most <paramref name="rowLimit"/> characters.</summary>
    internal static List<string> GroupWarnings(IEnumerable<string> warnings, int rowLimit)
    {
        List<string> rows = new List<string>();
        System.Text.StringBuilder current = new System.Text.StringBuilder();

        foreach (string warning in warnings)
        {
            string text = warning.Length > rowLimit ? warning.Substring(0, rowLimit) : warning;

            if (current.Length > 0 && current.Length + text.Length + 2 > rowLimit)
            {
                rows.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append("; ");
            }

            current.Append(text);
        }

        if (current.Length > 0)
        {
            rows.Add(current.ToString());
        }

        return rows;
    }

    private void TryLog(Helper helper, string text, Guid projectId, Guid sourceProjectId, string correlationId)
    {
        try
        {
            helper.createLog(text, true, projectId, sourceProjectId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "A log row could not be written to the destination environment; the copy is kept. CorrelationId: {CorrelationId}.",
                correlationId);
        }
    }

    /// <summary>
    /// The user to notify is the owner (or creator) of the SOURCE project, matched to their account
    /// in the destination. Best effort: a copy never fails because the notification did.
    /// </summary>
    private void TrySendNotification(
        IOrganizationService destinationService,
        string destinationUrl,
        Entity sourceProjectHeader,
        Entity project,
        ReferenceResolver resolver,
        bool succeeded,
        string correlationId)
    {
        try
        {
            foreach (string attribute in new[] { "ownerid", "createdby" })
            {
                EntityReference? reference = sourceProjectHeader.GetAttributeValue<EntityReference>(attribute);

                if (reference == null || !string.Equals(reference.LogicalName, "systemuser", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EntityReference? recipient = resolver.Resolve(reference);

                if (recipient != null)
                {
                    new Helper(destinationService, destinationUrl).SendProjectCopiedNotification(
                        destinationService,
                        recipient.Id,
                        project,
                        succeeded);

                    return;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "The copy notification could not be sent. CorrelationId: {CorrelationId}.",
                correlationId);
        }
    }
}
