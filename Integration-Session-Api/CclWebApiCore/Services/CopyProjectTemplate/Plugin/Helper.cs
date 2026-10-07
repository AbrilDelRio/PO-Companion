using System;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectTemplateV4Plugin
{
    public class Helper
    {
        private readonly IOrganizationService service = null;
        private readonly IPluginExecutionContext context = null;
        private readonly string recordUrlPrefix;

        public Helper(IOrganizationService service, IPluginExecutionContext context)
        {
            this.service = service;
            this.context = context;
            this.recordUrlPrefix =
                $"{context.OrganizationName}.crm.dynamics.com/main.aspx?pagetype=entityrecord&etn=msdyn_project&id=";
        }

        public string[] getTableFields(string logicalName)
        {
            return MetadataCache.GetCreatableFields(service, logicalName);
        }

        public Entity buildLog(string logDescription, bool isCompleted, Guid? newProject, Guid? templateProject, bool showOnLog = true)
        {
            Entity newLog = new Entity("mfd_pocopyprojectlog");
            newLog["mfd_description"] = logDescription;
            newLog["mfd_successfullycompleted"] = new OptionSetValue(isCompleted ? 1 : 0);
            newLog["mfd_createdupdatedrecordurl"] = recordUrlPrefix + newProject;
            newLog["mfd_sourcerecordurl"] = recordUrlPrefix + templateProject;
            newLog["mfd_showonlog"] = showOnLog;

            return newLog;
        }

        public void createLog(string logDescription, bool isCompleted, Guid? newProject, Guid? templateProject, bool showOnLog = true)
        {
            service.Create(buildLog(logDescription, isCompleted, newProject, templateProject, showOnLog));
        }

        public Entity buildCopyProjectTaskRelationLog(Entity targetProject, Entity targetPOTask, EntityReference sourceProjet, Entity sourcePOTask)
        {
            Entity newLog = new Entity("mfd_pocopyprojecttaskrelation");
            newLog["mfd_targetproject"] = targetProject.ToEntityReference();
            newLog["mfd_targetpotask"] = targetPOTask.ToEntityReference();
            newLog["mfd_sourceproject"] = sourceProjet;
            newLog["mfd_sourcepotask"] = sourcePOTask.ToEntityReference();

            return newLog;
        }

        public Entity createTeamMemberRelationLog(Entity targetProject, Entity targetTeamMember, EntityReference sourceProjet, Entity sourceTeamMember)
        {
            Entity newLog = new Entity("mfd_pocopyteammemberrelation");
            newLog["mfd_targetproject"] = targetProject.ToEntityReference();
            newLog["mfd_targetteammember"] = targetTeamMember.ToEntityReference();
            newLog["mfd_sourceproject"] = sourceProjet;
            newLog["mfd_sourceteammember"] = sourceTeamMember.ToEntityReference();

            return newLog;
        }

        public Entity getPOCopyProjectTaskRelation(EntityReference targeProject, EntityReference targetTask)
        {
            QueryExpression query = new QueryExpression("mfd_pocopyprojecttaskrelation")
            {
                ColumnSet = new ColumnSet("mfd_sourcepotask", "mfd_sourceproject"),
                NoLock = true,
                TopCount = 1
            };

            query.Criteria.AddCondition("mfd_targetproject", ConditionOperator.Equal, targeProject.Id);
            query.Criteria.AddCondition("mfd_targetpotask", ConditionOperator.Equal, targetTask.Id);

            return service.RetrieveMultiple(query).Entities.FirstOrDefault();
        }

        public void deleteCopyProjectTaskRelations(EntityReference targetProject)
        {
            QueryExpression query = new QueryExpression("mfd_pocopyprojecttaskrelation")
            {
                ColumnSet = new ColumnSet(false)
            };

            query.Criteria.AddCondition("mfd_targetproject", ConditionOperator.Equal, targetProject.Id);

            BulkDeleteRequest request = new BulkDeleteRequest
            {
                JobName = $"CopyProjectTemplate cleanup - {targetProject.Id}",
                QuerySet = new QueryExpression[] { query },
                StartDateTime = DateTime.UtcNow,
                ToRecipients = new Guid[] { },
                CCRecipients = new Guid[] { },
                SendEmailNotification = false,
                RecurrencePattern = string.Empty
            };

            service.Execute(request);
        }

        internal Guid SendProjectCopiedNotification(IOrganizationService service, Entity project, bool succeed = true)
        {
            string projectName = project.GetAttributeValue<string>("msdyn_subject") ?? "Project";
            string title = "Project copied successfully";
            string body = $"The project \"{projectName}\" has been copied successfully.";
            int iconType = 100000001;
            if (!succeed)
            {
                iconType = 100000002;
                title = "Project copy failed";
                body = $"The project could not be copied due to an unexpected error. Please try again or contact your administrator if the problem persists.";
            }


            OrganizationRequest request = new OrganizationRequest
            {
                RequestName = "SendAppNotification",
                Parameters = new ParameterCollection
                {
                    ["Title"] = title,
                    ["Body"] = body,
                    ["Recipient"] = new EntityReference("systemuser", service.Retrieve("msdyn_project", project.Id, new ColumnSet("createdby")).GetAttributeValue<EntityReference>("createdby").Id),
                    ["IconType"] = new OptionSetValue(iconType),
                    ["ToastType"] = new OptionSetValue(200000000),
                    ["Priority"] = new OptionSetValue(200000000)
                }
            };

            OrganizationResponse response = service.Execute(request);
            if (response.Results.Contains("NotificationId"))
            {
                return (Guid)response.Results["NotificationId"];
            }
            return Guid.Empty;
        }
    }
}
