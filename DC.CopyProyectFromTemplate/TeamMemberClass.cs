using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectFromTemplate
{
    public class TeamMemberClass
    {
        private readonly Dictionary<Guid, Guid> teamMemberDictionary = new Dictionary<Guid, Guid>();
        private readonly IOrganizationService service;
        private readonly IOrganizationService sourceService;
        private readonly Helper helper;
        private readonly Helper sourceHelper;

        public TeamMemberClass(IOrganizationService service, string dataverseUrl)
            : this(service, service, dataverseUrl, dataverseUrl)
        {
        }

        /// <summary>Cross-environment: team members are read from <paramref name="sourceService"/> and created in <paramref name="service"/>.</summary>
        public TeamMemberClass(IOrganizationService sourceService, IOrganizationService service, string sourceDataverseUrl, string dataverseUrl)
        {
            this.sourceService = sourceService;
            this.service = service;
            helper = new Helper(service, dataverseUrl, sourceDataverseUrl);
            sourceHelper = new Helper(sourceService, sourceDataverseUrl);
        }

        internal void deleteTeamMembers(Entity newProject)
        {
            if (newProject == null)
            {
                throw new ArgumentNullException(nameof(newProject));
            }

            try
            {
                QueryExpression query = new QueryExpression("msdyn_projectteam")
                {
                    ColumnSet = new ColumnSet(false),
                    NoLock = true
                };

                query.Criteria.AddCondition("msdyn_project",ConditionOperator.Equal,newProject.Id);

                List<Entity> teamMembers = QueryHelper.RetrieveAll(service, query);

                if (teamMembers == null || teamMembers.Count == 0)
                {
                    helper.createLog("No existing Team Members found to delete.",true,newProject.Id,null);
                    return;
                }

                foreach (Entity teamMember in teamMembers)
                {
                        service.Delete("msdyn_projectteam",teamMember.Id);
                }
            }
            catch (Exception ex)
            {
                helper.createLog($"Error on Team Member (DeleteTeamMembers) function: {ex.Message}",false,newProject.Id,null);
                throw;
            }
        }

        /// <summary>The team members a project already has, by bookable resource.</summary>
        private Dictionary<Guid, Guid> GetExistingMembersByResource(Entity project)
        {
            QueryExpression query = new QueryExpression("msdyn_projectteam")
            {
                ColumnSet = new ColumnSet("msdyn_bookableresourceid"),
                NoLock = true
            };

            query.Criteria.AddCondition("msdyn_project", ConditionOperator.Equal, project.Id);

            Dictionary<Guid, Guid> existing = new Dictionary<Guid, Guid>();

            foreach (Entity member in QueryHelper.RetrieveAll(service, query))
            {
                EntityReference resource = member.GetAttributeValue<EntityReference>("msdyn_bookableresourceid");

                if (resource != null && !existing.ContainsKey(resource.Id))
                {
                    existing[resource.Id] = member.Id;
                }
            }

            return existing;
        }

        public Guid? GetTeamMemberGuid(Guid templateGuid)
        {
            Guid newGuid;
            if (teamMemberDictionary.TryGetValue(templateGuid, out newGuid))
            {
                return newGuid;
            }

            return null;
        }

        internal static HashSet<Guid> GetGenericResourceIds(IOrganizationService service)
        {
            QueryExpression query = new QueryExpression("bookableresource")
            {
                ColumnSet = new ColumnSet(false),
                NoLock = true
            };

            query.Criteria.AddCondition("resourcetype", ConditionOperator.Equal, 1);

            List<Entity> resources = QueryHelper.RetrieveAll(service, query);

            HashSet<Guid> ids = new HashSet<Guid>();
            for (int i = 0; i < resources.Count; i++)
            {
                ids.Add(resources[i].Id);
            }

            return ids;
        }

        internal Dictionary<Guid, EntityReference> copyTeamMembers(int copyType, EntityReference projectTemplate, Entity newProject, int copyMode, BatchWriter writer, ReferenceResolver? resolver = null)
        {
            try
            {
                string[] teamMemberFields = resolver == null
                    ? helper.getTableFields("msdyn_projectteam")
                    : helper.GetCommonTableFields(sourceHelper, "msdyn_projectteam");

                QueryExpression query = new QueryExpression("msdyn_projectteam")
                {
                    ColumnSet = new ColumnSet(teamMemberFields),
                    NoLock = true
                };

                query.Criteria.AddCondition("msdyn_project", ConditionOperator.Equal, projectTemplate.Id);

                List<Entity> teamMembers = QueryHelper.RetrieveAll(sourceService, query);

                // Same-environment copy into an existing project: its team is cleared first.
                // Copy to another environment: the project was just created through the API, and Project
                // Operations refuses a direct Delete on msdyn_projectteam. Whoever it already carries
                // (the default project manager, for example) stays, and is reused below instead of
                // being created twice.
                Dictionary<Guid, Guid>? existingByResource = null;

                if (resolver == null)
                {
                    deleteTeamMembers(newProject);
                }
                else
                {
                    existingByResource = GetExistingMembersByResource(newProject);
                }

                if (copyMode == 2)
                {
                    helper.createLog("Filtering only Generic Resources", true, newProject.Id, projectTemplate.Id);

                    HashSet<Guid> genericTeamMemberIds = GetGenericResourceIds(sourceService);

                    List<Entity> generic = new List<Entity>(teamMembers.Count);
                    for (int i = 0; i < teamMembers.Count; i++)
                    {
                        EntityReference bookableResource = teamMembers[i].GetAttributeValue<EntityReference>("msdyn_bookableresourceid");

                        if (bookableResource != null && genericTeamMemberIds.Contains(bookableResource.Id))
                        {
                            generic.Add(teamMembers[i]);
                        }
                    }

                    teamMembers = generic;
                }

                helper.createLog($"Copying {teamMembers.Count} Team Members", true, newProject.Id, projectTemplate.Id);

                EntityReference newProjectRef = newProject.ToEntityReference();

                for (int i = 0; i < teamMembers.Count; i++)
                {
                    Entity team = teamMembers[i];
                    Entity newTeam = new Entity("msdyn_projectteam");

                    foreach (string field in teamMemberFields)
                    {
                        object value;
                        if (team.Attributes.TryGetValue(field, out value))
                        {
                            newTeam[field] = value;
                        }
                    }

                    if (resolver != null)
                    {
                        resolver.Translate(newTeam);

                        EntityReference sourceResource = team.GetAttributeValue<EntityReference>("msdyn_bookableresourceid");
                        EntityReference mappedResource = newTeam.GetAttributeValue<EntityReference>("msdyn_bookableresourceid");

                        if (sourceResource != null && mappedResource == null)
                        {
                            // A team member is its resource: without an equivalent one there is nothing to create.
                            resolver.AddWarning("a team member was skipped: its resource has no equivalent in the destination environment");
                            continue;
                        }

                        Guid existingTeamMemberId;
                        if (mappedResource != null && existingByResource!.TryGetValue(mappedResource.Id, out existingTeamMemberId))
                        {
                            // Already on the new project: the tasks' assignments will point at it.
                            teamMemberDictionary[team.Id] = existingTeamMemberId;
                            continue;
                        }
                    }

                    newTeam["msdyn_project"] = newProjectRef;
                    newTeam.Id = Guid.NewGuid();

                    teamMemberDictionary[team.Id] = newTeam.Id;

                    writer.Create(newTeam);
                }

                writer.Flush();

                Dictionary<Guid, EntityReference> teamMemberMap =
                    new Dictionary<Guid, EntityReference>(teamMemberDictionary.Count);

                foreach (KeyValuePair<Guid, Guid> pair in teamMemberDictionary)
                {
                    teamMemberMap[pair.Key] = new EntityReference("msdyn_projectteam", pair.Value);
                }

                return teamMemberMap;
            }
            catch (Exception ex)
            {
                helper.createLog($"Error on Team Member (copyTeamMembers)function: {ex.Message}", false, newProject.Id, projectTemplate.Id);
                throw;
            }
        }
    }
}
