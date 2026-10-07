namespace CclWebApi.Models
{
    public class CopyProjectTemplateResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public Guid TargetProjectId { get; set; }
        public string TargetLogicalName { get; set; } = string.Empty;
        public string ConfigurationCode { get; set; } = string.Empty;
        public string OrganizationName { get; set; } = string.Empty;
    }
}
