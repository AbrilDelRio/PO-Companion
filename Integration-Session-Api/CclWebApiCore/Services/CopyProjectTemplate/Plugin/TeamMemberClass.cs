using System;
using System.Collections.Generic;
using CclCrmProxyCore.Helpers;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectTemplateV4Plugin
{
    public class TeamMemberClass
    {
        private readonly Dictionary<Guid, Guid> teamMemberDictionary = new Dictionary<Guid, Guid>();
        private readonly IOrganizationService service = null;
        private readonly Helper helper = null;

        public TeamMemberClass(IOrganizationService service, IPluginExecutionContext context)
        {
            this.service = service;
            helper = new Helper(this.service, context);
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

                query.Criteria.AddCondition("msdyn_project", ConditionOperator.Equal, newProject.Id);

                List<Entity> teamMembers = QueryHelper.RetrieveAll(service, query);

                if (teamMembers == null || teamMembers.Count == 0)
                {
                    helper.createLog("No existing Team Members found to delete.", true, newProject.Id, null);
                    return;
                }

                foreach (Entity teamMember in teamMembers)
                {
                    service.Delete("msdyn_projectteam", teamMember.Id);
                }
            }
            catch (Exception ex)
            {
                helper.createLog($"Error on Team Member (DeleteTeamMembers) function: {ex.Message}", false, newProject.Id, null);
                throw;
            }
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

        internal Dictionary<Guid, EntityReference> copyTeamMembers(int copyType, EntityReference projectTemplate, Entity newProject, int copyMode, bool generateTeamMember, BatchWriter writer)
        {
            try
            {
                string[] teamMemberFields = helper.getTableFields("msdyn_projectteam");

                QueryExpression query = new QueryExpression("msdyn_projectteam")
                {
                    ColumnSet = new ColumnSet(teamMemberFields),
                    NoLock = true
                };

                query.Criteria.AddCondition("msdyn_project", ConditionOperator.Equal, projectTemplate.Id);

                LinkEntity existingTargetMember = query.AddLink("msdyn_projectteam", "msdyn_bookableresourceid", "msdyn_bookableresourceid", JoinOperator.LeftOuter);

                existingTargetMember.EntityAlias = "existingTargetMember";
                existingTargetMember.Columns = new ColumnSet(false);

                existingTargetMember.LinkCriteria.AddCondition("msdyn_project", ConditionOperator.Equal, newProject.Id);

                query.Criteria.AddCondition(new ConditionExpression
                {
                    EntityName = existingTargetMember.EntityAlias,
                    AttributeName = "msdyn_projectteamid",
                    Operator = ConditionOperator.Null
                });

                List<Entity> teamMembers = QueryHelper.RetrieveAll(service, query);

                //deleteTeamMembers(newProject);

                if (copyMode == 2)
                {
                    helper.createLog("Filtering only Generic Resources", true, newProject.Id, projectTemplate.Id);

                    HashSet<Guid> genericTeamMemberIds = GetGenericResourceIds(service);

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
                        if (team.Attributes.TryGetValue(field, out value) && field != "msdyn_resourcerequirementid")
                        {
                            newTeam[field] = value;
                        }
                    }

                    newTeam["msdyn_project"] = newProjectRef;
                    newTeam.Id = Guid.NewGuid();

                    teamMemberDictionary[team.Id] = newTeam.Id;

                    writer.Create(newTeam);
                    //writer.Create(helper.createTeamMemberRelationLog(newProject, newTeam, projectTemplate, team));
                }

                writer.Flush();

                Dictionary<Guid, EntityReference> teamMemberMap = new Dictionary<Guid, EntityReference>(teamMemberDictionary.Count);

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

        internal void createProjectManger(Entity newProject, BatchWriter writer)
        {
            try
            {
                string[] teamMemberFields = helper.getTableFields("msdyn_projectteam");
                EntityReference projectManager = newProject.GetAttributeValue<EntityReference>("msdyn_projectmanager");

                if (projectManager == null)
                {
                    Entity targetProject = service.Retrieve(newProject.LogicalName, newProject.Id, new ColumnSet("msdyn_projectmanager"));
                    projectManager = targetProject.GetAttributeValue<EntityReference>("msdyn_projectmanager");
                }

                if (projectManager == null)
                {
                    throw new InvalidPluginExecutionException("The target project does not have a Project Manager assigned.");
                }

                QueryExpression resourceQuery = new QueryExpression("bookableresource")
                {
                    ColumnSet = new ColumnSet("name"),
                    NoLock = true,
                    TopCount = 1
                };

                resourceQuery.Criteria.AddCondition("userid", ConditionOperator.Equal, projectManager.Id);
                resourceQuery.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

                EntityCollection resources = service.RetrieveMultiple(resourceQuery);

                if (resources.Entities.Count == 0)
                {
                    throw new InvalidPluginExecutionException($"No active Bookable Resource was found for Project Manager '{projectManager.Name}'.");
                }

                Entity projectManagerResource = resources.Entities[0];

                string projectManagerName = projectManagerResource.GetAttributeValue<string>("name");

                if (string.IsNullOrWhiteSpace(projectManagerName))
                {
                    throw new InvalidPluginExecutionException("The Project Manager Bookable Resource does not have a name.");
                }

                QueryExpression parameterQuery = new QueryExpression("msdyn_projectparameter")
                {
                    ColumnSet = new ColumnSet("msdyn_projectmanagerrole"),
                    NoLock = true,
                    TopCount = 1
                };

                EntityCollection parameterResults = service.RetrieveMultiple(parameterQuery);

                if (parameterResults.Entities.Count == 0)
                {
                    throw new InvalidPluginExecutionException("Project Parameters configuration was not found.");
                }

                EntityReference projectManagerRole = parameterResults.Entities[0].GetAttributeValue<EntityReference>("msdyn_projectmanagerrole");

                if (projectManagerRole == null)
                {
                    throw new InvalidPluginExecutionException("The default Project Manager role is not configured in Project Parameters.");
                }

                Entity projectManagerTeamMember = new Entity("msdyn_projectteam");
                projectManagerTeamMember["msdyn_projectteamid"] = Guid.NewGuid();
                projectManagerTeamMember["msdyn_project"] = newProject.ToEntityReference();
                projectManagerTeamMember["msdyn_bookableresourceid"] = projectManagerResource.ToEntityReference();
                projectManagerTeamMember["msdyn_resourcecategory"] = projectManagerRole;
                projectManagerTeamMember["msdyn_name"] = projectManagerName;

                writer.Create(projectManagerTeamMember);
                writer.Flush();
            }
            catch
            {
                throw;
            }
        }
    }
}
