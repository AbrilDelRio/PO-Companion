using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectFromTemplate
{
    public class ProjectClass
    {
        private IOrganizationService service = null;
        private readonly IOrganizationService sourceService = null;
        private Helper helper = null;
        private Helper sourceHelper = null;

        /// <summary>
        /// "Scheduling Engine" of a Project Operations project. It is chosen when the project is created
        /// and cannot be changed afterwards. An externally scheduled project accepts tasks created
        /// directly with Dataverse; one scheduled by Project for the web refuses them
        /// ("You cannot directly do 'Create' operation to 'msdyn_projecttask'"). The copy therefore
        /// always carries it over, whether or not the copy configuration lists it.
        /// </summary>
        internal const string SchedulingEngineColumn = "msdyn_scheduler";

        public ProjectClass(IOrganizationService service, string dataverseUrl)
            : this(service, service, dataverseUrl, dataverseUrl)
        {
        }

        /// <summary>Cross-environment: reads from <paramref name="sourceService"/>, writes to <paramref name="service"/>.</summary>
        public ProjectClass(IOrganizationService sourceService, IOrganizationService service, string sourceDataverseUrl, string dataverseUrl)
        {
            this.sourceService = sourceService;
            this.service = service;
            helper = new Helper(service, dataverseUrl, sourceDataverseUrl);
            sourceHelper = new Helper(sourceService, sourceDataverseUrl);
        }

        /// <summary>
        /// Creates the copy of a project in the destination environment (new id) from the columns the
        /// PO copy configuration lists. msdyn_subject is always copied; a start date is always set,
        /// because the dates of the copied tasks are calculated from it. Columns that are not creatable
        /// in the destination are skipped and reported as warnings.
        /// </summary>
        internal Entity CreateProjectFromSource(List<Entity> entityPo, Guid sourceProjectId, string sourceLookupFieldToExclude, ReferenceResolver resolver)
        {
            Entity newProject = null;

            try
            {
                string[] columns = entityPo.Select(x => x.GetAttributeValue<string>("mfd_field")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
                // The scheduling engine only exists in Project Operations: ask for it when the source has it.
                bool sourceHasEngine = new HashSet<string>(sourceHelper.getTableFields("msdyn_project"), StringComparer.OrdinalIgnoreCase)
                    .Contains(SchedulingEngineColumn);

                List<string> alwaysRead = new List<string> { "msdyn_subject", "msdyn_scheduledstart" };
                if (sourceHasEngine)
                {
                    alwaysRead.Add(SchedulingEngineColumn);
                }

                string[] sourceColumns = columns.Concat(alwaysRead).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                Entity sourceProject = sourceService.Retrieve("msdyn_project", sourceProjectId, new ColumnSet(sourceColumns));
                HashSet<string> creatable = new HashSet<string>(helper.getTableFields("msdyn_project"), StringComparer.OrdinalIgnoreCase);

                newProject = new Entity("msdyn_project");

                foreach (string poField in columns)
                {
                    if (string.Equals(poField, sourceLookupFieldToExclude, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    object value;
                    if (!sourceProject.Attributes.TryGetValue(poField, out value))
                    {
                        continue;
                    }

                    if (!creatable.Contains(poField))
                    {
                        resolver.AddWarning("project column '" + poField + "' cannot be set when creating a project in the destination environment");
                        continue;
                    }

                    newProject[poField] = value;
                }

                object schedulingEngine;
                if (!newProject.Contains(SchedulingEngineColumn) &&
                    sourceProject.Attributes.TryGetValue(SchedulingEngineColumn, out schedulingEngine))
                {
                    if (creatable.Contains(SchedulingEngineColumn))
                    {
                        newProject[SchedulingEngineColumn] = schedulingEngine;
                    }
                    else
                    {
                        resolver.AddWarning("the scheduling engine of the source project cannot be set in the destination environment: the new project uses its default engine and may refuse tasks created directly");
                    }
                }

                if (!newProject.Contains("msdyn_subject"))
                {
                    newProject["msdyn_subject"] = sourceProject.GetAttributeValue<string>("msdyn_subject");
                }

                resolver.Translate(newProject);

                if (!newProject.Contains("msdyn_scheduledstart"))
                {
                    newProject["msdyn_scheduledstart"] = sourceProject.GetAttributeValue<DateTime?>("msdyn_scheduledstart") ?? DateTime.UtcNow.Date;
                }

                DropInvertedDatePairs(newProject, resolver);

                newProject.Id = service.Create(newProject);

                try
                {
                    return service.Retrieve("msdyn_project", newProject.Id, new ColumnSet("msdyn_subject", "msdyn_scheduledstart", "createdby", "ownerid"));
                }
                catch (Exception)
                {
                    // The project exists but the caller never learns its id: remove it here, or a retry
                    // would leave a second, orphaned copy in the destination.
                    try { service.Delete("msdyn_project", newProject.Id); } catch (Exception) { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                // Dataverse reports "the due date can't be earlier than the start date" without saying
                // which columns: listing the dates that were sent is what makes it diagnosable.
                string message = ex.Message + DescribeDates(newProject);
                helper.createLog($"Error creating the project in the destination environment: {message}", false, null, sourceProjectId);
                throw new InvalidPluginExecutionException(message, ex);
            }
        }

        private static readonly string[][] DatePairs =
        {
            new[] { "msdyn_scheduledstart", "msdyn_finish" },
            new[] { "msdyn_actualstart", "msdyn_actualend" }
        };

        /// <summary>
        /// A start copied from the source and an end that falls before it (typical when the start is
        /// replaced by today's date) is refused by Dataverse. The end is left out: the project's finish
        /// is calculated from its tasks anyway. The omission is reported as a warning.
        /// </summary>
        private static void DropInvertedDatePairs(Entity project, ReferenceResolver resolver)
        {
            foreach (string[] pair in DatePairs)
            {
                DateTime? start = project.GetAttributeValue<DateTime?>(pair[0]);
                DateTime? end = project.GetAttributeValue<DateTime?>(pair[1]);

                if (start.HasValue && end.HasValue && end.Value < start.Value)
                {
                    project.Attributes.Remove(pair[1]);
                    resolver.AddWarning("project column '" + pair[1] + "' was left out: it was earlier than '" + pair[0] + "'");
                }
            }
        }

        private static string DescribeDates(Entity project)
        {
            if (project == null)
            {
                return string.Empty;
            }

            List<string> dates = new List<string>();

            foreach (KeyValuePair<string, object> attribute in project.Attributes)
            {
                if (attribute.Value is DateTime date)
                {
                    dates.Add(attribute.Key + "=" + date.ToString("yyyy-MM-dd HH:mm") + "Z");
                }
            }

            return dates.Count == 0 ? string.Empty : " (dates sent: " + string.Join(", ", dates) + ")";
        }

        public Entity CopyProjectFields(List<Entity> entityPo,Entity target,Guid sourceProjectId,string sourceLookupFieldToExclude)
        {
            try
            {
                string[] columns = entityPo.Select(x => x.GetAttributeValue<string>("mfd_field")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();

                Entity targetFull = service.Retrieve(target.LogicalName, target.Id, new ColumnSet(columns));
                Entity sourceProject = service.Retrieve(targetFull.LogicalName,sourceProjectId,new ColumnSet(columns));

                foreach (var poField in columns)
                {
                    object value;
                    if (!string.Equals(poField,sourceLookupFieldToExclude,StringComparison.OrdinalIgnoreCase) && sourceProject.Attributes.TryGetValue(poField, out value))
                    {
                        targetFull[poField] = value;
                    }
                }

                if (targetFull.Attributes.Count > 0)
                {
                    service.Update(targetFull);
                }

                return targetFull;
            }
            catch (Exception ex)
            {
                helper.createLog($"Error copying project fields: {ex.Message}",false,target?.Id,sourceProjectId);
                throw;
            }
        }

        public OptionSetValue setProjectStatus(string projectStatus)
        {
            int status = 1;
            RetrieveAttributeRequest request = new RetrieveAttributeRequest
            {
                EntityLogicalName = "msdyn_project",
                LogicalName = "statuscode",
                RetrieveAsIfPublished = true
            };

            RetrieveAttributeResponse response = (RetrieveAttributeResponse)service.Execute(request);

            StatusAttributeMetadata metadata = (StatusAttributeMetadata)response.AttributeMetadata;

            foreach (StatusOptionMetadata option in metadata.OptionSet.Options)
            {
                if (option.Label.UserLocalizedLabel.Label.Equals(projectStatus))
                {
                    status = (int)option.Value;
                    helper.createLog($"status selected value: {option.Value}", true, null, null);
                    break;
                }
            }
            return new OptionSetValue(status);
        }

    }
}
