using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectFromTemplate
{
    public class Helper
    {
        private readonly IOrganizationService service;
        private readonly string recordUrlPrefix;
        private readonly string sourceRecordUrlPrefix;
        private readonly string environmentKey;

        /// <param name="dataverseUrl">Environment the service is connected to (record links of the project this log is about).</param>
        /// <param name="sourceDataverseUrl">Environment the SOURCE project lives in, when it is not the same one.</param>
        public Helper(IOrganizationService service, string dataverseUrl, string? sourceDataverseUrl = null)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));

            if (string.IsNullOrWhiteSpace(dataverseUrl))
            {
                throw new ArgumentException("Dataverse URL was not provided.", nameof(dataverseUrl));
            }

            this.environmentKey = dataverseUrl.TrimEnd('/').ToLowerInvariant();

            this.recordUrlPrefix =
                $"{dataverseUrl.TrimEnd('/')}/main.aspx?pagetype=entityrecord&etn=msdyn_project&id=";

            this.sourceRecordUrlPrefix = string.IsNullOrWhiteSpace(sourceDataverseUrl)
                ? this.recordUrlPrefix
                : $"{sourceDataverseUrl.TrimEnd('/')}/main.aspx?pagetype=entityrecord&etn=msdyn_project&id=";
        }

        public string[] getTableFields(string logicalName)
        {
            return MetadataCache.GetCreatableFields(service, logicalName, environmentKey);
        }

        /// <summary>
        /// Columns that are creatable in this environment AND exist in the source one: the copy reads
        /// them from the source and writes them here.
        /// </summary>
        public string[] GetCommonTableFields(Helper sourceHelper, string logicalName)
        {
            HashSet<string> inSource = new HashSet<string>(
                sourceHelper.getTableFields(logicalName),
                StringComparer.OrdinalIgnoreCase);

            return getTableFields(logicalName).Where(inSource.Contains).ToArray();
        }

        /// <summary>mfd_description accepts at most 2000 characters; a longer text is refused by Dataverse.</summary>
        public const int MaxLogDescriptionLength = 2000;

        public static string? FitLogDescription(string? text)
        {
            if (text == null || text.Length <= MaxLogDescriptionLength)
            {
                return text;
            }

            return text.Substring(0, MaxLogDescriptionLength - 3) + "...";
        }

        public Entity buildLog(string logDescription, bool isCompleted, Guid? newProject, Guid? templateProject, bool showOnLog = true)
        {
            Entity newLog = new Entity("mfd_pocopyprojectlog");
            newLog["mfd_description"] = FitLogDescription(logDescription);
            newLog["mfd_successfullycompleted"] = new OptionSetValue(isCompleted ? 1 : 0);
            newLog["mfd_createdupdatedrecordurl"] = recordUrlPrefix + newProject;
            newLog["mfd_sourcerecordurl"] = sourceRecordUrlPrefix + templateProject;
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

        public Entity? getPOCopyProjectTaskRelation(EntityReference targeProject, EntityReference targetTask)
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

        public void deleteAllRecords(string tableName)
        {
            QueryExpression query = new QueryExpression(tableName)
            {
                ColumnSet = new ColumnSet(false)
            };

            BulkDeleteRequest request = new BulkDeleteRequest
            {
                JobName = "Delete all records",
                QuerySet = new QueryExpression[] { query },
                StartDateTime = DateTime.Now,
                ToRecipients = new Guid[] { },
                CCRecipients = new Guid[] { },
                SendEmailNotification = false,
                RecurrencePattern = string.Empty
            };

            service.Execute(request);
        }

        internal Guid SendProjectCopiedNotification(IOrganizationService service,Guid recipientUserId,Entity project, bool succeed = true)
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
                    ["Recipient"] = new EntityReference("systemuser", recipientUserId),
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
