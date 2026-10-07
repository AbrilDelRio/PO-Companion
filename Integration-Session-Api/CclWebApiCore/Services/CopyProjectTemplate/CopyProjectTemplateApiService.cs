using CclWebApi.Models;
using DC.CopyProyectTemplateV4Plugin;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace CclWebApi.Services.CopyProjectTemplate
{
    /// <summary>
    /// API host adapter for the original project-template plug-in implementation.
    /// The plug-in classes are executed through an emulated Dataverse service provider.
    /// </summary>
    internal sealed class CopyProjectTemplateApiService
    {
        private const string DefaultTargetLogicalName = "msdyn_project";
        private const string DefaultConfigurationCode = "1000";

        private readonly IOrganizationService _service;
        private readonly string? _crmConnectionString;

        public CopyProjectTemplateApiService(IOrganizationService service,string? crmConnectionString)
        {
            _service = service;
            _crmConnectionString = crmConnectionString;
        }

        public CopyProjectTemplateExecutionResult Execute(CopyProjectTemplateRequest request,string transactionId)
        {
            string targetLogicalName = string.IsNullOrWhiteSpace(request.TargetLogicalName) ? DefaultTargetLogicalName : request.TargetLogicalName.Trim();

            DataverseOrganizationServiceFactory serviceFactory = new DataverseOrganizationServiceFactory(_service);

            IOrganizationService executionService = serviceFactory.CreateOrganizationService(request.UserId);

            string organizationName = ResolveOrganizationName(request.OrganizationName,executionService);

            RemoteExecutionContext context = new RemoteExecutionContext
            {
                OrganizationName = organizationName,
                UserId = request.UserId ?? Guid.Empty
            };

            Helper helper = new Helper(executionService, context);
            Guid? templateProjectId = null;

            helper.createLog($"CopyProjectTemplate started. TransactionId: {transactionId}",false,request.TargetProjectId,templateProjectId);

            try
            {
                Entity target = BuildTarget(executionService,targetLogicalName,request.TargetProjectId,request.ConfigurationCode,out string configurationCode);

                templateProjectId = TryResolveTemplateProjectId(executionService,targetLogicalName,request.TargetProjectId,configurationCode);

                context.InputParameters["Target"] = target;

                PluginExecutionServiceProvider serviceProvider = new PluginExecutionServiceProvider(context, serviceFactory);
                IPlugin plugin = new CopyProyectTemplateV4Plugin();

                plugin.Execute(serviceProvider);

                helper.createLog($"CopyProjectTemplate completed. TransactionId: {transactionId}",true,target.Id,templateProjectId);

                return new CopyProjectTemplateExecutionResult
                {
                    TargetProjectId = target.Id,
                    TargetLogicalName = target.LogicalName,
                    ConfigurationCode = configurationCode,
                    OrganizationName = organizationName,
                    TemplateProjectId = templateProjectId
                };
            }
            catch (Exception ex)
            {
                TryCreateFailureLog(helper,transactionId,request.TargetProjectId,templateProjectId,ex);

                throw;
            }
        }

        private static void TryCreateFailureLog(Helper helper,string transactionId,Guid targetProjectId,Guid? templateProjectId,Exception exception)
        {
            try
            {
                helper.createLog($"CopyProjectTemplate failed. TransactionId: {transactionId}. Error: {exception.Message}",false,targetProjectId,templateProjectId);
            }
            catch
            {

            }
        }

        private static Entity BuildTarget(IOrganizationService service,string targetLogicalName,Guid targetProjectId,string? requestedConfigurationCode,out string configurationCode)
        {
            Entity target = new Entity(targetLogicalName) { Id = targetProjectId };

            configurationCode = requestedConfigurationCode?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(configurationCode))
            {
                configurationCode = ResolveConfigurationCode(service,targetLogicalName,targetProjectId);
            }

            if (!string.IsNullOrWhiteSpace(configurationCode) && !string.Equals(configurationCode,DefaultConfigurationCode,StringComparison.Ordinal))
            {
                Entity configuration = new Entity("mfd_pocopyprojectconfiguration");
                configuration["mfd_code"] = configurationCode;
                target["mfd_poconfiguration"] = configuration;
            }

            return target;
        }

        private static string ResolveConfigurationCode(IOrganizationService service,string targetLogicalName,Guid targetProjectId)
        {
            Entity project = service.Retrieve(targetLogicalName,targetProjectId,new ColumnSet("mfd_poconfiguration"));

            EntityReference? configurationReference = project.GetAttributeValue<EntityReference>("mfd_poconfiguration");

            if (configurationReference == null)
            {
                return DefaultConfigurationCode;
            }

            Entity configuration = service.Retrieve(configurationReference.LogicalName,configurationReference.Id,new ColumnSet("mfd_code"));

            return configuration.GetAttributeValue<string>("mfd_code") ?? DefaultConfigurationCode;
        }

        private static Guid? TryResolveTemplateProjectId(IOrganizationService service,string targetLogicalName,Guid targetProjectId,string configurationCode)
        {
            try
            {
                PoConfiguration poConfiguration = new PoConfiguration(service);
                Entity configuration = poConfiguration.getPOConfiguration(configurationCode);
                string? templateField = configuration?.GetAttributeValue<string>("mfd_templatefield");

                if (string.IsNullOrWhiteSpace(templateField))
                {
                    return null;
                }

                Entity project = service.Retrieve(targetLogicalName,targetProjectId,new ColumnSet(templateField));

                return project.GetAttributeValue<EntityReference>(templateField)?.Id;
            }
            catch
            {
                return null;
            }
        }

        private string ResolveOrganizationName(string? requestedOrganizationName,IOrganizationService service)
        {
            string? normalized = NormalizeOrganizationName(requestedOrganizationName);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            try
            {
                RetrieveCurrentOrganizationResponse response = (RetrieveCurrentOrganizationResponse)service.Execute(new RetrieveCurrentOrganizationRequest());

                normalized = NormalizeOrganizationName(response.Detail?.UniqueName);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }
            catch
            {

            }

            normalized = ResolveOrganizationNameFromConnectionString(_crmConnectionString);
            return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
        }

        private static string? ResolveOrganizationNameFromConnectionString(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }

            string[] segments = connectionString.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string segment in segments)
            {
                int separatorIndex = segment.IndexOf('=');
                if (separatorIndex <= 0 || separatorIndex >= segment.Length - 1)
                {
                    continue;
                }

                string key = segment.Substring(0, separatorIndex).Trim();
                if (!key.Equals("Url", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("ServiceUri", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("ServerUrl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = segment.Substring(separatorIndex + 1).Trim();
                return NormalizeOrganizationName(value);
            }

            return null;
        }

        private static string? NormalizeOrganizationName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string normalized = value.Trim().TrimEnd('/');

            if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
            {
                normalized = uri.Host;
            }

            const string crmSuffix = ".crm.dynamics.com";
            int suffixIndex = normalized.IndexOf(
                crmSuffix,
                StringComparison.OrdinalIgnoreCase);

            if (suffixIndex > 0)
            {
                normalized = normalized.Substring(0, suffixIndex);
            }

            int firstDotIndex = normalized.IndexOf('.');
            if (firstDotIndex > 0)
            {
                normalized = normalized.Substring(0, firstDotIndex);
            }

            return normalized;
        }
    }

    internal sealed class CopyProjectTemplateExecutionResult
    {
        public Guid TargetProjectId { get; set; }
        public string TargetLogicalName { get; set; } = string.Empty;
        public string ConfigurationCode { get; set; } = string.Empty;
        public string OrganizationName { get; set; } = string.Empty;
        public Guid? TemplateProjectId { get; set; }
    }
}
