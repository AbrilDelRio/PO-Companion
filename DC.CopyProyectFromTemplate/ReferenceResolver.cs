using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectFromTemplate
{
    /// <summary>
    /// Translates lookups of a record read in the SOURCE environment into lookups that exist in the
    /// DESTINATION environment, where the same record has another id. Order of attempts:
    ///   1. the same id exists in the destination (reference data deployed with a solution);
    ///   2. a system user is matched by Entra object id, then domain name, then e-mail;
    ///   3. a bookable resource is matched by its (mapped) user, then by name;
    ///   4. anything else is matched by its primary name column.
    /// A reference that cannot be mapped is left out of the record and reported in
    /// <see cref="Warnings"/>: the copy goes on without it instead of failing on a dangling id.
    /// Records that belong to ONE project (project, task, bucket, team member...) are never
    /// matched by name: that could hook the copy into another project, so the code that copies
    /// them sets those references explicitly.
    /// </summary>
    internal sealed class ReferenceResolver
    {
        private const int MaxWarnings = 50;

        private static readonly HashSet<string> ProjectScoped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "msdyn_project",
            "msdyn_projecttask",
            "msdyn_projectbucket",
            "msdyn_projectteam",
            "msdyn_projectsprint",
            "msdyn_resourceassignment",
            "msdyn_projecttaskdependency"
        };

        private readonly IOrganizationService source;
        private readonly IOrganizationService target;
        private readonly Dictionary<string, EntityReference?> resolved = new Dictionary<string, EntityReference?>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> primaryNameAttributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ReferenceResolver(IOrganizationService source, IOrganizationService target)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.target = target ?? throw new ArgumentNullException(nameof(target));
        }

        /// <summary>One line per reference (or field) that was left out of the copy.</summary>
        public List<string> Warnings { get; } = new List<string>();

        public int UnmappedCount { get; private set; }

        public void AddWarning(string message)
        {
            UnmappedCount++;

            if (reported.Add(message) && Warnings.Count < MaxWarnings)
            {
                Warnings.Add(message);
            }
        }

        /// <summary>Returns the destination equivalent of a source reference, or null when there is none.</summary>
        public EntityReference? Resolve(EntityReference? sourceReference)
        {
            if (sourceReference == null)
            {
                return null;
            }

            string key = sourceReference.LogicalName + ":" + sourceReference.Id.ToString("N");

            EntityReference? cached;
            if (resolved.TryGetValue(key, out cached))
            {
                return cached;
            }

            EntityReference? result = ResolveCore(sourceReference);
            resolved[key] = result;
            return result;
        }

        /// <summary>
        /// Replaces every lookup of the entity by its destination equivalent and removes the ones
        /// without equivalent. Call it right after copying the source values and BEFORE setting the
        /// references the copy owns (project, bucket, parent task, team member).
        /// </summary>
        public void Translate(Entity entity)
        {
            foreach (string attribute in entity.Attributes.Keys.ToList())
            {
                object value = entity[attribute];

                if (value is EntityReference reference)
                {
                    EntityReference? mapped = Resolve(reference);

                    if (mapped == null)
                    {
                        entity.Attributes.Remove(attribute);
                    }
                    else
                    {
                        entity[attribute] = mapped;
                    }
                }
                else if (value is EntityCollection)
                {
                    // Party lists point at records of the source environment.
                    entity.Attributes.Remove(attribute);
                }
            }
        }

        private EntityReference? ResolveCore(EntityReference sourceReference)
        {
            string logicalName = sourceReference.LogicalName;

            if (ProjectScoped.Contains(logicalName))
            {
                return null;
            }

            if (Exists(logicalName, sourceReference.Id))
            {
                return new EntityReference(logicalName, sourceReference.Id);
            }

            switch (logicalName)
            {
                case "systemuser":
                    return ResolveUser(sourceReference);
                case "bookableresource":
                    return ResolveBookableResource(sourceReference);
                default:
                    return ResolveByName(sourceReference);
            }
        }

        private EntityReference? ResolveUser(EntityReference sourceReference)
        {
            Entity? user = TryRetrieve(
                source,
                "systemuser",
                sourceReference.Id,
                new ColumnSet("azureactivedirectoryobjectid", "domainname", "internalemailaddress", "fullname"));

            if (user == null)
            {
                AddWarning("user " + sourceReference.Id + " was not found in the source environment");
                return null;
            }

            EntityReference? match = null;

            Guid? entraObjectId = user.GetAttributeValue<Guid?>("azureactivedirectoryobjectid");
            if (entraObjectId.HasValue && entraObjectId.Value != Guid.Empty)
            {
                match = FindOne(target, "systemuser", "azureactivedirectoryobjectid", entraObjectId.Value);
            }

            string domainName = user.GetAttributeValue<string>("domainname");
            if (match == null && !string.IsNullOrWhiteSpace(domainName))
            {
                match = FindOne(target, "systemuser", "domainname", domainName);
            }

            string email = user.GetAttributeValue<string>("internalemailaddress");
            if (match == null && !string.IsNullOrWhiteSpace(email))
            {
                match = FindOne(target, "systemuser", "internalemailaddress", email);
            }

            if (match == null)
            {
                string label = user.GetAttributeValue<string>("fullname") ?? domainName ?? sourceReference.Id.ToString();
                AddWarning("user '" + label + "' has no equivalent in the destination environment");
            }

            return match;
        }

        private EntityReference? ResolveBookableResource(EntityReference sourceReference)
        {
            Entity? resource = TryRetrieve(source, "bookableresource", sourceReference.Id, new ColumnSet("name", "userid"));

            if (resource == null)
            {
                AddWarning("bookable resource " + sourceReference.Id + " was not found in the source environment");
                return null;
            }

            EntityReference? sourceUser = resource.GetAttributeValue<EntityReference>("userid");
            if (sourceUser != null)
            {
                EntityReference? mappedUser = Resolve(sourceUser);
                if (mappedUser != null)
                {
                    EntityReference? byUser = FindOne(target, "bookableresource", "userid", mappedUser.Id);
                    if (byUser != null)
                    {
                        return byUser;
                    }
                }
            }

            string name = resource.GetAttributeValue<string>("name");
            EntityReference? byName = string.IsNullOrWhiteSpace(name)
                ? null
                : FindOne(target, "bookableresource", "name", name);

            if (byName == null)
            {
                AddWarning("resource '" + (name ?? sourceReference.Id.ToString()) + "' has no equivalent in the destination environment");
            }

            return byName;
        }

        private EntityReference? ResolveByName(EntityReference sourceReference)
        {
            string logicalName = sourceReference.LogicalName;
            string? nameAttribute = GetPrimaryNameAttribute(logicalName);

            if (string.IsNullOrWhiteSpace(nameAttribute))
            {
                AddWarning(logicalName + " " + sourceReference.Id + " cannot be matched by name and does not exist in the destination");
                return null;
            }

            Entity? sourceRecord = TryRetrieve(source, logicalName, sourceReference.Id, new ColumnSet(nameAttribute));
            string? name = sourceRecord == null ? null : sourceRecord.GetAttributeValue<string>(nameAttribute);

            if (string.IsNullOrWhiteSpace(name))
            {
                AddWarning(logicalName + " " + sourceReference.Id + " has no name to match in the destination");
                return null;
            }

            EntityReference? match = FindOne(target, logicalName, nameAttribute, name);

            if (match == null)
            {
                AddWarning(logicalName + " '" + name + "' has no equivalent in the destination environment");
            }

            return match;
        }

        private string? GetPrimaryNameAttribute(string logicalName)
        {
            string? cached;
            if (primaryNameAttributes.TryGetValue(logicalName, out cached))
            {
                return cached;
            }

            string? attribute = null;

            try
            {
                RetrieveEntityResponse response = (RetrieveEntityResponse)source.Execute(new RetrieveEntityRequest
                {
                    LogicalName = logicalName,
                    EntityFilters = EntityFilters.Entity,
                    RetrieveAsIfPublished = false
                });

                attribute = response.EntityMetadata.PrimaryNameAttribute;
            }
            catch (Exception)
            {
                attribute = null;
            }

            primaryNameAttributes[logicalName] = attribute;
            return attribute;
        }

        private bool Exists(string logicalName, Guid id)
        {
            return TryRetrieve(target, logicalName, id, new ColumnSet(false)) != null;
        }

        private static Entity? TryRetrieve(IOrganizationService service, string logicalName, Guid id, ColumnSet columns)
        {
            try
            {
                return service.Retrieve(logicalName, id, columns);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static EntityReference? FindOne(IOrganizationService service, string logicalName, string attribute, object value)
        {
            QueryExpression query = new QueryExpression(logicalName)
            {
                ColumnSet = new ColumnSet(false),
                NoLock = true,
                TopCount = 1
            };

            query.Criteria.AddCondition(attribute, ConditionOperator.Equal, value);

            try
            {
                Entity? found = service.RetrieveMultiple(query).Entities.FirstOrDefault();
                return found == null ? null : new EntityReference(logicalName, found.Id);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
