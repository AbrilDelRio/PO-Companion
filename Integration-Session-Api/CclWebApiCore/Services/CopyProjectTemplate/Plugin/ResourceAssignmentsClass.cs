using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectTemplateV4Plugin
{
    public class ResourceAssignmentsClass
    {
        private readonly IOrganizationService service = null;
        private readonly Helper helper = null;

        public ResourceAssignmentsClass(IOrganizationService service, IPluginExecutionContext context)
        {
            this.service = service;
            helper = new Helper(service, context);
        }

        internal void CopyResourceAssignments(Entity newTask, Entity templateTask, CopyContext ctx)
        {
            try
            {
                //ctx.Writer.Create(helper.buildLog("copying resource assignments", true, null, null));
                bool genericOnly = ctx.CopyResourcesValue == 3;
                if (ctx.CopyResourcesValue != 1 && !genericOnly)
                {
                    //ctx.Writer.Create(helper.buildLog("Resource assignments will not be copied", true, null, null));
                    return;
                }

                // GET ORIGINAL RESOURCE ASSIGNMENTS
                QueryExpression query = new QueryExpression("msdyn_resourceassignment")
                {
                    ColumnSet = new ColumnSet(ctx.ResourceColumns),
                    NoLock = true
                };

                query.Criteria.AddCondition("msdyn_taskid", ConditionOperator.Equal, templateTask.Id);
                List<Entity> originalResourceAssignments;
                Stopwatch queryStopwatch = Stopwatch.StartNew();
                try
                {
                    originalResourceAssignments = service.RetrieveMultiple(query).Entities.ToList();
                }
                catch (Exception ex)
                {
                    throw new InvalidPluginExecutionException(
                        $"RetrieveMultiple msdyn_resourceassignment for source task {templateTask.Id} " +
                        $"failed after {queryStopwatch.Elapsed.TotalSeconds:F1} seconds: {ex.Message}", ex);
                }

                if (originalResourceAssignments.Count == 0)
                {
                    return;
                }

                EntityReference newTaskRef = newTask.ToEntityReference();

                // CREATE NEW RESOURCE ASSIGNMENTS
                foreach (Entity resource in originalResourceAssignments)
                {
                    //Entity resource = originalResourceAssignments[i];

                    if (genericOnly)
                    {
                        EntityReference bookableResource = resource.GetAttributeValue<EntityReference>("msdyn_bookableresourceid");

                        if (bookableResource == null || !ctx.GenericResourceIds.Contains(bookableResource.Id))
                        {
                            continue;
                        }
                    }

                    Entity resourceAssignment = new Entity("msdyn_resourceassignment");

                    foreach (string field in ctx.ResourceColumns)
                    {
                        object value;
                        if (resource.Attributes.TryGetValue(field, out value))
                        {
                            resourceAssignment[field] = value;
                        }
                    }

                    resourceAssignment["msdyn_taskid"] = newTaskRef;
                    resourceAssignment["msdyn_projectid"] = ctx.TargetProjectRef;
                    if (genericOnly)
                    {
                        resourceAssignment["msdyn_plannedwork"] = null;
                    }
                    EntityReference sourceTeamMember = resource.GetAttributeValue<EntityReference>("msdyn_projectteamid");

                    if (sourceTeamMember != null)
                    {
                        EntityReference targetTeamMember;
                        if (ctx.TeamMemberMap.TryGetValue(sourceTeamMember.Id, out targetTeamMember))
                        {
                            ctx.Writer.Create(helper.buildLog($"Target Member Found: {targetTeamMember.Name}", true, null, null));
                            resourceAssignment["msdyn_projectteamid"] = targetTeamMember;
                        }
                    }

                    resourceAssignment.Id = Guid.NewGuid();
                    ctx.Writer.Create(resourceAssignment);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidPluginExecutionException(ex.Message, ex);
            }
        }
    }
}
