using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace DC.CopyProyectFromTemplate
{
    internal sealed class CopyContext
    {
        public Entity TargetProject = null!;
        public EntityReference TargetProjectRef = null!;
        public EntityReference SourceProject = null!;
        public EntityReference ProjectBucketRef = null!;
        public string[] TaskColumns = null!;
        public string[] ResourceColumns = null!;
        public int CopyResourcesValue;
        public int CopyDatesMethod;
        public DateTime? SourceProjectStart;
        public DateTime? TargetProjectStart;
        public decimal HoursPerWorkingDay = 8m;
        public HashSet<Guid> GenericResourceIds = null!;
        public Dictionary<Guid, EntityReference> TeamMemberMap = null!;
        public Dictionary<Guid, List<Entity>> ChildrenByParent = null!;
        public Dictionary<Guid, List<Entity>> LabelsByTask = null!;
        public BatchWriter Writer = null!;

        /// <summary>Only set in a cross-environment copy: maps source lookups to destination ones.</summary>
        public ReferenceResolver? Resolver;
    }
}
