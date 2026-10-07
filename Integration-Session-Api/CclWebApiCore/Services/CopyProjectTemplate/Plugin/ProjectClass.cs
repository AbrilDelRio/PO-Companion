using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectTemplateV4Plugin
{
    public class ProjectClass
    {
        private IOrganizationService service = null;
        private Helper helper = null;

        public ProjectClass(IOrganizationService service, IPluginExecutionContext context)
        {
            this.service = service;
            helper = new Helper(service, context);
        }

        public Entity copyProject(List<Entity> entityPo, Entity target, string templateTableField)
        {
            try
            {
                //CREATE AN ARRAY TO ENSURE THE SAME NUMBER OF FIELDS IN BOTH THE BASE PROJECT AND THE TEMPLATE
                List<string> columnList = entityPo.Select(x => x.GetAttributeValue<string>("mfd_field")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();

                if (!columnList.Contains(templateTableField))
                {
                    columnList.Add(templateTableField);
                }

                string[] columns = columnList.ToArray();

                //GET THE NEW PROJECT
                Entity targetFull = service.Retrieve(target.LogicalName, target.Id, new ColumnSet(columns));

                //GET THE TEMPLATE PROJECT ID
                EntityReference templatePoGUID = targetFull.GetAttributeValue<EntityReference>(templateTableField);
                if (templatePoGUID == null)
                {
                    throw new InvalidPluginExecutionException($"Project '{target.Id}' has no template assigned in '{templateTableField}'. Nothing to copy.");
                }

                //GET THE TEMPLATE PROJECT
                Entity baseProject = service.Retrieve(targetFull.LogicalName, templatePoGUID.Id, new ColumnSet(columns));

                //SET THE FIELDS FOR THE PROJECT TEMPLATE OF THE NEW PROJECT
                //Entity delta = new Entity(targetFull.LogicalName) { Id = targetFull.Id };

                foreach (var poField in columns)
                {
                    object value;
                    if (poField != templateTableField && baseProject.Attributes.TryGetValue(poField, out value))
                    {
                        targetFull[poField] = value;
                        //delta[poField] = value;
                    }
                }

                //targetFull["statuscode"] = setProjectStatus("Project copying");

                //SAVE THE CHANGES
                if (targetFull.Attributes.Count > 0)
                {
                    service.Update(targetFull);
                }

                return targetFull;
            }
            catch (Exception ex)
            {
                helper.createLog($"Error creating project: {ex.Message}", false, target.Id, null);
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
