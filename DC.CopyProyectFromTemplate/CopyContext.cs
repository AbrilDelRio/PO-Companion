using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace DC.CopyProyectFromTemplate
{
    internal sealed class CopyContext
    {
        public Entity TargetProject;
        public EntityReference TargetProjectRef;
        public EntityReference SourceProject;
        public EntityReference ProjectBucketRef;
        public string[] TaskColumns;
        public string[] ResourceColumns;
        public int CopyResourcesValue;
        public int CopyDatesMethod;
        public DateTime? SourceProjectStart;
        public DateTime? TargetProjectStart;
        public decimal HoursPerWorkingDay = 8m;
        public HashSet<Guid> GenericResourceIds;
        public Dictionary<Guid, EntityReference> TeamMemberMap;
        public Dictionary<Guid, List<Entity>> ChildrenByParent;
        public Dictionary<Guid, List<Entity>> LabelsByTask;
        public BatchWriter Writer;

        /// <summary>Only set in a cross-environment copy: maps source lookups to destination ones.</summary>
        public ReferenceResolver Resolver;
    }
}
