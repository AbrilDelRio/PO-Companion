using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectTemplateV4Plugin
{
    public class CopyProyectTemplateV4Plugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);
            ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            PoConfiguration poConfiguration = new PoConfiguration(service);
            ProjectTaskClass POTask = new ProjectTaskClass(service, context);
            ProjectClass POProject = new ProjectClass(service, context);
            TeamMemberClass TeamMember = new TeamMemberClass(service, context);
            Helper helper = new Helper(service, context);
            BatchWriter writer = new BatchWriter(service);

            if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
            {
                throw new InvalidPluginExecutionException("Target was not provided.");
            }

            try
            {
                //RETRIEVE THE CONFIGURATION CODE POR "PO CONFIGURATION TABLE"
                string poCodeConfiguration = poConfiguration.getConfigurationCode(target);

                //RETRIEVE THE MAIN CONFIGURATION BY THE SELECTED CODE
                Entity entityPoConfig = poConfiguration.getPOConfiguration(poCodeConfiguration);

                if (entityPoConfig != null)
                {
                    //GET THE FIELD THAT IS THE TEMPLATE TO OBTAIN THE PROJECT TEMPLATE
                    string templateTableField = entityPoConfig.GetAttributeValue<string>("mfd_templatefield");

                    if (templateTableField != "")
                    {
                        List<Entity> entityPo = poConfiguration.getProjectFieldsToCopy(entityPoConfig, service);

                        Entity targetFull = POProject.copyProject(entityPo, target, templateTableField);

                        EntityReference templateProject = targetFull.GetAttributeValue<EntityReference>(templateTableField);
                        helper.createLog("Project Copied", true, targetFull.Id, templateProject.Id);

                        OptionSetValue copyTeamMembers = entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copyteammembers");
                        bool generateProjectManager = entityPoConfig.GetAttributeValue<bool>("mfd_generateprojectmanager");
                        Dictionary<Guid, EntityReference> teamMemberMap = null;

                        TeamMember.deleteTeamMembers(targetFull);

                        if (generateProjectManager)
                        {
                            TeamMember.createProjectManger(targetFull, writer);
                        }

                        if (copyTeamMembers.Value != 0)
                        {
                            teamMemberMap = TeamMember.copyTeamMembers(copyTeamMembers.Value, templateProject, targetFull, copyTeamMembers.Value, generateProjectManager, writer);
                        }

                        OptionSetValue copyTasks = entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copytask");

                        //CHECK IF THE TASKS NEED TO BE COPIED
                        if (copyTasks.Value != 0)
                        {
                            string[] tasksColumns = poConfiguration.getProjectTaskFieldsToCopy(entityPoConfig);
                            string[] resourceAssignmentsColumns = helper.getTableFields("msdyn_resourceassignment");

                            List<Entity> templateTasks = POTask.getTasks(tasksColumns, templateProject.Id);
                            helper.createLog("tasks retrived", true, targetFull.Id, templateProject.Id);

                            Entity projectBucket = POTask.createProjectBucket(targetFull);
                            helper.createLog("project bucket created", true, targetFull.Id, templateProject.Id);

                            // FILTER FOR ONLY PARENT TASKS
                            List<Entity> parentTasks;
                            Dictionary<Guid, List<Entity>> childrenByParent = ProjectTaskClass.GroupChildren(templateTasks, out parentTasks);

                            OptionSetValue copyResources = entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copyresources");

                            OptionSetValue copyDatesMethod = entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_copydatesmethod");

                            helper.createLog($"Copy dates method value: {copyDatesMethod.Value}", true, targetFull.Id, templateProject.Id);

                            Entity sourceProjectStartDate = service.Retrieve("msdyn_project", templateProject.Id, new ColumnSet("msdyn_scheduledstart"));
                            Entity targetProjectStartDate = service.Retrieve("msdyn_project", targetFull.Id, new ColumnSet("msdyn_scheduledstart"));

                            CopyContext ctx = new CopyContext
                            {
                                TargetProject = targetFull,
                                TargetProjectRef = targetFull.ToEntityReference(),
                                SourceProject = templateProject,
                                ProjectBucketRef = projectBucket.ToEntityReference(),
                                TaskColumns = tasksColumns,
                                ResourceColumns = resourceAssignmentsColumns,
                                CopyResourcesValue = copyResources?.Value ?? 1,
                                TeamMemberMap = teamMemberMap ?? new Dictionary<Guid, EntityReference>(0),
                                ChildrenByParent = childrenByParent,
                                LabelsByTask = POTask.GetLabelsByTask(templateProject.Id),
                                Writer = writer,
                                CopyDatesMethod = copyDatesMethod.Value,
                                TargetProjectStart = targetProjectStartDate.GetAttributeValue<DateTime?>("msdyn_scheduledstart"),
                                SourceProjectStart = sourceProjectStartDate.GetAttributeValue<DateTime?>("msdyn_scheduledstart")
                            };

                            if (ctx.CopyResourcesValue == 3)
                            {
                                ctx.GenericResourceIds = TeamMemberClass.GetGenericResourceIds(service);
                            }

                            if (copyTasks.Value == 1)
                            {
                                //COPY PARENT TASKS AND CHILD TASKS
                                foreach (var task in parentTasks)
                                {
                                    POTask.CopyTaskRecursive(task, null, ctx, true);
                                }
                            }
                            else if (copyTasks.Value == 2)
                            {
                                //ONLY COPY PARENT TASKS
                                foreach (var task in parentTasks)
                                {
                                    POTask.CopyTaskRecursive(task, null, ctx, false);
                                }
                            }
                            else if (copyTasks.Value == 3)
                            {
                                // FILTER FOR ONLY MILESTONES
                                foreach (var task in templateTasks)
                                {
                                    if (task.GetAttributeValue<bool>("msdyn_ismilestone"))
                                    {
                                        POTask.CopyTaskRecursive(task, null, ctx, true);
                                    }
                                }
                            }

                            writer.Flush();

                            helper.createLog("Copying Dependencies", true, targetFull.Id, templateProject.Id);
                            POTask.CopyDependencies(templateTableField, targetFull, writer);
                            writer.Flush();

                            if (copyTasks.Value == 1)
                            {
                                OptionSetValue createRequirements = entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_generaterequirements");
                                if (createRequirements != null && createRequirements.Value == 1)
                                {

                                }
                            }
                        }

                        writer.Flush();
                        helper.createLog("Project copied successfully", true, targetFull.Id, templateProject.Id);
                        helper.SendProjectCopiedNotification(service, target);
                    }
                }
            }
            catch (Exception ex)
            {
                writer.DiscardPending();
                try
                {
                    helper.SendProjectCopiedNotification(service, target, false);
                }
                catch (Exception notificationException)
                {
                    tracingService?.Trace("Failure notification could not be sent: {0}", notificationException.Message);
                }
                throw new InvalidPluginExecutionException($"Error: {ex.Message}", ex);
            }
        }
    }
}
