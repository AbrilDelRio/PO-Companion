using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectTemplateV4Plugin
{
    public class ProjectTaskClass
    {
        private static readonly List<Entity> NoChildren = new List<Entity>(0);
        private readonly Dictionary<Guid, Guid> taskMapping = new Dictionary<Guid, Guid>();
        private readonly IOrganizationService service = null;
        private readonly Helper helper = null;
        private readonly ResourceAssignmentsClass resourceAssignments = null;

        public ProjectTaskClass(IOrganizationService service, IPluginExecutionContext context)
        {
            this.service = service;
            helper = new Helper(service, context);
            resourceAssignments = new ResourceAssignmentsClass(service, context);
        }

        //GET THE TASKS
        public List<Entity> getTasks(string[] tasksColumns, Guid baseProject)
        {
            try
            {
                QueryExpression query = new QueryExpression("msdyn_projecttask")
                {
                    ColumnSet = new ColumnSet(tasksColumns),
                    NoLock = true
                };

                query.Criteria.AddCondition("msdyn_project", ConditionOperator.Equal, baseProject);
                query.AddOrder("msdyn_displaysequence", OrderType.Ascending);

                return QueryHelper.RetrieveAll(service, query);
            }
            catch (Exception ex)
            {
                helper.createLog($"error creating Project Task (getTasks)function: {ex.Message}", false, baseProject, null);
                throw;
            }
        }

        public Entity createProjectBucket(Entity targetFull)
        {
            //BEFORE CREATE THE TASKS, MAKE SURE THERE ARE A BUCKET CREATED
            Entity projectBucket = new Entity("msdyn_projectbucket");
            projectBucket["msdyn_project"] = targetFull.ToEntityReference();
            projectBucket["msdyn_name"] = "Bucket 1";
            projectBucket.Id = service.Create(projectBucket);

            return projectBucket;
        }

        public static Dictionary<Guid, List<Entity>> GroupChildren(List<Entity> allTasks, out List<Entity> rootTasks)
        {
            Dictionary<Guid, List<Entity>> childrenByParent = new Dictionary<Guid, List<Entity>>(allTasks.Count);
            rootTasks = new List<Entity>();

            for (int i = 0; i < allTasks.Count; i++)
            {
                Entity task = allTasks[i];
                EntityReference parent = task.GetAttributeValue<EntityReference>("msdyn_parenttask");

                if (parent == null)
                {
                    rootTasks.Add(task);
                    continue;
                }

                List<Entity> siblings;
                if (!childrenByParent.TryGetValue(parent.Id, out siblings))
                {
                    siblings = new List<Entity>();
                    childrenByParent[parent.Id] = siblings;
                }

                siblings.Add(task);
            }

            return childrenByParent;
        }

        public Dictionary<Guid, List<Entity>> GetLabelsByTask(Guid templateProjectId)
        {
            QueryExpression query = new QueryExpression("msdyn_projecttasktolabel")
            {
                ColumnSet = new ColumnSet("msdyn_projecttaskid", "msdyn_projectlabelid"),
                NoLock = true
            };

            LinkEntity link = query.AddLink("msdyn_projecttask", "msdyn_projecttaskid", "msdyn_projecttaskid");
            link.LinkCriteria.AddCondition("msdyn_project", ConditionOperator.Equal, templateProjectId);

            List<Entity> rows = QueryHelper.RetrieveAll(service, query);

            Dictionary<Guid, List<Entity>> labelsByTask = new Dictionary<Guid, List<Entity>>(rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                EntityReference taskRef = rows[i].GetAttributeValue<EntityReference>("msdyn_projecttaskid");
                if (taskRef == null)
                {
                    continue;
                }

                List<Entity> labels;
                if (!labelsByTask.TryGetValue(taskRef.Id, out labels))
                {
                    labels = new List<Entity>(1);
                    labelsByTask[taskRef.Id] = labels;
                }

                labels.Add(rows[i]);
            }

            return labelsByTask;
        }

        internal void CopyTaskRecursive(Entity templateTask, EntityReference? newParentTask, CopyContext ctx, bool recurse)
        {
            try
            {
                Entity newTask = new Entity("msdyn_projecttask");

                foreach (string field in ctx.TaskColumns)
                {
                    object value;
                    if (field != "msdyn_parenttask" && templateTask.Attributes.TryGetValue(field, out value))
                    {
                        newTask[field] = value;
                    }
                }

                newTask["msdyn_project"] = ctx.TargetProjectRef;
                newTask["msdyn_projectbucket"] = ctx.ProjectBucketRef;

                ApplyTaskDates(templateTask, newTask, ctx);

                if (newParentTask != null)
                {
                    newTask["msdyn_parenttask"] = newParentTask;
                }

                newTask.Id = Guid.NewGuid();
                taskMapping[templateTask.Id] = newTask.Id;

                ctx.Writer.Create(newTask);
                //ctx.Writer.Create(helper.buildCopyProjectTaskRelationLog(ctx.TargetProject, newTask, ctx.SourceProject, templateTask));

                EntityReference newTaskRef = newTask.ToEntityReference();
                List<Entity> templateLabels;
                if (ctx.LabelsByTask.TryGetValue(templateTask.Id, out templateLabels))
                {
                    for (int i = 0; i < templateLabels.Count; i++)
                    {
                        Entity newTaskLabel = new Entity("msdyn_projecttasktolabel");

                        newTaskLabel["msdyn_projecttaskid"] = newTaskRef;
                        newTaskLabel["msdyn_projectlabelid"] = templateLabels[i]["msdyn_projectlabelid"];

                        ctx.Writer.Create(newTaskLabel);
                    }
                }

                resourceAssignments.CopyResourceAssignments(newTask, templateTask, ctx);
                ctx.Writer.Create(helper.buildLog($"New project task created: {newTask.GetAttributeValue<string>("msdyn_subject")}",true, ctx.TargetProject.Id, null));

                //IF THE TASK HAS CHILD TASKS AND COPYTASK IS 1, CONTINUES HERE COPYING THE CHILD TASKS
                if (!recurse)
                {
                    return;
                }

                List<Entity> children;
                if (!ctx.ChildrenByParent.TryGetValue(templateTask.Id, out children))
                {
                    children = NoChildren;
                }

                for (int i = 0; i < children.Count; i++)
                {
                    CopyTaskRecursive(children[i], newTaskRef, ctx, true);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    helper.createLog(
                        $"error creating Project Task (CopyTaskRecursive), source task {templateTask.Id}: {ex.Message}",
                        false, ctx.TargetProject.Id, null);
                }
                catch (Exception logException)
                {
                    System.Diagnostics.Trace.TraceError("CopyTaskRecursive failure log could not be written: {0}", logException.Message);
                }
                throw;
            }
        }

        private void ApplyTaskDates(Entity templateTask, Entity newTask, CopyContext ctx)
        {
            try
            {
                switch (ctx.CopyDatesMethod)
                {
                    case 1:
                        CalculateDatesFromDuration(templateTask, newTask, ctx);
                        break;

                    case 2:
                        CalculateDatesFromEffort(templateTask, newTask, ctx);
                        break;

                    default:
                        //helper.createLog($"Unknown Copy Dates Method value: {ctx.CopyDatesMethod}. " + "Original task dates will be copied.", false, ctx.TargetProject.Id, templateTask.Id);
                        break;
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        private void CalculateDatesFromDuration(Entity templateTask, Entity newTask, CopyContext ctx)
        {
            try
            {
                if (!ctx.TargetProjectStart.HasValue)
                {
                    throw new InvalidPluginExecutionException("The target project does not have msdyn_scheduledstart configured.");
                }

                DateTime newStart = CalculateNewTaskStart(templateTask, ctx.SourceProjectStart, ctx.TargetProjectStart.Value);

                decimal durationInDays = GetDecimalValue(templateTask, "msdyn_duration");

                if (durationInDays < 0)
                {
                    durationInDays = 0;
                }

                DateTime newEnd = newStart.AddDays(Convert.ToDouble(durationInDays));

                newTask["msdyn_scheduledstart"] = newStart;
                newTask["msdyn_scheduledend"] = newEnd;
                newTask["msdyn_duration"] = Convert.ToDouble(durationInDays);

                //helper.createLog($"Task dates calculated using duration. " + $"Task: {templateTask.Id}. " + $"Start: {newStart:O}. " + $"End: {newEnd:O}. " + $"Duration: {durationInDays} days.", true, ctx.TargetProject.Id, templateTask.Id);
            }
            catch (Exception)
            {
                throw;
            }
        }

        private void CalculateDatesFromEffort(Entity templateTask, Entity newTask, CopyContext ctx)
        {
            try
            {
                if (!ctx.TargetProjectStart.HasValue)
                {
                    throw new InvalidPluginExecutionException("The target project does not have msdyn_scheduledstart configured.");
                }

                if (ctx.HoursPerWorkingDay <= 0)
                {
                    throw new InvalidPluginExecutionException("HoursPerWorkingDay must be greater than zero.");
                }

                DateTime newStart = CalculateNewTaskStart(templateTask, ctx.SourceProjectStart, ctx.TargetProjectStart.Value);

                decimal effortInHours = GetDecimalValue(templateTask, "msdyn_effort");

                if (effortInHours < 0)
                {
                    effortInHours = 0;
                }

                decimal durationInDays = effortInHours / ctx.HoursPerWorkingDay;

                DateTime newEnd = newStart.AddDays(Convert.ToDouble(durationInDays));

                newTask["msdyn_scheduledstart"] = newStart;
                newTask["msdyn_scheduledend"] = newEnd;
                newTask["msdyn_duration"] = Convert.ToDouble(durationInDays);
                newTask["msdyn_effort"] = effortInHours;

                //helper.createLog($"Task dates calculated using effort. " + $"Task: {templateTask.Id}. " + $"Start: {newStart:O}. " + $"End: {newEnd:O}. " + $"Effort: {effortInHours} hours. " + $"Duration: {durationInDays} days.", true, ctx.TargetProject.Id, templateTask.Id);
            }
            catch (Exception)
            {
                throw;
            }
        }

        private static decimal GetDecimalValue(Entity entity, string attributeName)
        {
            try
            {
                if (!entity.Attributes.TryGetValue(attributeName, out object value) || value == null)
                {
                    return 0m;
                }

                switch (value)
                {
                    case decimal decimalValue:
                        return decimalValue;

                    case double doubleValue:
                        return Convert.ToDecimal(doubleValue);

                    case float floatValue:
                        return Convert.ToDecimal(floatValue);

                    case int intValue:
                        return intValue;

                    case long longValue:
                        return longValue;

                    case Money moneyValue:
                        return moneyValue.Value;

                    default:
                        return Convert.ToDecimal(value);
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        private static DateTime CalculateNewTaskStart(Entity templateTask, DateTime? sourceProjectStart, DateTime targetProjectStart)
        {
            try
            {
                DateTime? originalTaskStart = templateTask.GetAttributeValue<DateTime?>("msdyn_scheduledstart");

                if (!sourceProjectStart.HasValue || !originalTaskStart.HasValue)
                {
                    return targetProjectStart;
                }

                TimeSpan offset = originalTaskStart.Value - sourceProjectStart.Value;

                return targetProjectStart.Add(offset);
            }
            catch (Exception)
            {

                throw;
            }

        }

        internal void CopyDependencies(string templateTableField, Entity newProject, BatchWriter writer)
        {
            try
            {
                string[] dependenciesColumns = helper.getTableFields("msdyn_projecttaskdependency");

                QueryExpression query = new QueryExpression("msdyn_projecttaskdependency")
                {
                    ColumnSet = new ColumnSet(dependenciesColumns),
                    NoLock = true
                };

                query.Criteria.AddCondition("msdyn_project", ConditionOperator.Equal, newProject.GetAttributeValue<EntityReference>(templateTableField).Id);

                List<Entity> dependencies = QueryHelper.RetrieveAll(service, query);
                List<string> copyableColumns = new List<string>(dependenciesColumns.Length);
                foreach (string column in dependenciesColumns)
                {
                    if (column != "msdyn_predecessortask" && column != "msdyn_successortask")
                    {
                        copyableColumns.Add(column);
                    }
                }

                EntityReference newProjectRef = newProject.ToEntityReference();

                for (int i = 0; i < dependencies.Count; i++)
                {
                    Entity dependency = dependencies[i];

                    EntityReference predecessor = dependency.GetAttributeValue<EntityReference>("msdyn_predecessortask");
                    EntityReference successor = dependency.GetAttributeValue<EntityReference>("msdyn_successortask");

                    if (predecessor == null || successor == null) continue;
                    Guid newPredecessor;
                    Guid newSuccessor;
                    if (!taskMapping.TryGetValue(predecessor.Id, out newPredecessor)) continue;
                    if (!taskMapping.TryGetValue(successor.Id, out newSuccessor)) continue;

                    Entity newDependency = new Entity("msdyn_projecttaskdependency");

                    foreach (string column in copyableColumns)
                    {
                        object value;
                        if (dependency.Attributes.TryGetValue(column, out value))
                        {
                            newDependency[column] = value;
                        }
                    }

                    newDependency["msdyn_project"] = newProjectRef;
                    newDependency["msdyn_predecessortask"] = new EntityReference("msdyn_projecttask", newPredecessor);
                    newDependency["msdyn_successortask"] = new EntityReference("msdyn_projecttask", newSuccessor);

                    writer.Create(newDependency);
                }
            }
            catch (Exception)
            {

                throw;
            }
        }
    }
}
