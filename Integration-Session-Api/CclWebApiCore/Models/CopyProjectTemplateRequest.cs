namespace CclWebApi.Models
{
    /// <summary>
    /// Request used to execute the existing CopyProjectTemplate plug-in logic from the API.
    /// </summary>
    public class CopyProjectTemplateRequest
    {
        /// <summary>
        /// Existing target project that will receive the template data.
        /// </summary>
        public Guid TargetProjectId { get; set; }

        /// <summary>
        /// Logical name of the target table. Defaults to msdyn_project.
        /// </summary>
        public string TargetLogicalName { get; set; } = "msdyn_project";

        /// <summary>
        /// Optional PO configuration code. When omitted, the API reads mfd_poconfiguration
        /// from the target project and resolves its mfd_code. If the project has no configuration,
        /// the original plug-in default (1000) is preserved.
        /// </summary>
        public string? ConfigurationCode { get; set; }

        /// <summary>
        /// Optional Dataverse systemuserid. When supplied, Dataverse operations are executed
        /// on behalf of this user through ServiceClient.CallerId.
        /// </summary>
        public Guid? UserId { get; set; }

        /// <summary>
        /// Optional Dataverse organization unique name. Normally resolved automatically.
        /// </summary>
        public string? OrganizationName { get; set; }
    }
}
