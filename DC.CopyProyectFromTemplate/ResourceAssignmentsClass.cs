using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectFromTemplate
{
    public class ResourceAssignmentsClass
    {
        private readonly IOrganizationService sourceService = null;
        private readonly Helper helper = null;

        public ResourceAssignmentsClass(IOrganizationService service, string dataverseUrl)
            : this(service, service, dataverseUrl, dataverseUrl)
        {
        }

        /// <summary>Cross-environment: the assignments are read from <paramref name="sourceService"/>.</summary>
        public ResourceAssignmentsClass(IOrganizationService sourceService, IOrganizationService service, string sourceDataverseUrl, string dataverseUrl)
        {
            this.sourceService = sourceService;
            helper = new Helper(service, dataverseUrl, sourceDataverseUrl);
        }

        internal void CopyResourceAssignments(Entity newTask, Entity templateTask, CopyContext ctx)
        {
            try
            {

                bool genericOnly = ctx.CopyResourcesValue == 3;
                if (ctx.CopyResourcesValue != 1 && !genericOnly)
                {
                    return;
                }

                QueryExpression query = new QueryExpression("msdyn_resourceassignment")
                {
                    ColumnSet = new ColumnSet(ctx.ResourceColumns),
                    NoLock = true
                };

                query.Criteria.AddCondition("msdyn_taskid", ConditionOperator.Equal, templateTask.Id);
                DataCollection<Entity> originalResourceAssignments = sourceService.RetrieveMultiple(query).Entities;

                if (originalResourceAssignments.Count == 0)
                {
                    return;
                }

                EntityReference newTaskRef = newTask.ToEntityReference();

                for (int i = 0; i < originalResourceAssignments.Count; i++)
                {
                    Entity resource = originalResourceAssignments[i];

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

                    if (ctx.Resolver != null)
                    {
                        // The team member of the source project means nothing in the destination: the
                        // mapped one is set below. Same for task and project (set explicitly).
                        resourceAssignment.Attributes.Remove("msdyn_projectteamid");
                        ctx.Resolver.Translate(resourceAssignment);

                        // An assignment is its resource: without an equivalent one it cannot be created.
                        if (resource.GetAttributeValue<EntityReference>("msdyn_bookableresourceid") != null &&
                            resourceAssignment.GetAttributeValue<EntityReference>("msdyn_bookableresourceid") == null)
                        {
                            ctx.Resolver.AddWarning("a resource assignment was skipped: its resource has no equivalent in the destination environment");
                            continue;
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
