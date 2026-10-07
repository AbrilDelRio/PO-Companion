using Microsoft.Xrm.Sdk;

namespace CclWebApi.Services.CopyProjectTemplate
{
    /// <summary>
    /// Supplies the same services that Dataverse provides to an IPlugin implementation while
    /// reusing the API's authenticated Dataverse connection.
    /// </summary>
    internal sealed class PluginExecutionServiceProvider : IServiceProvider
    {
        private readonly IPluginExecutionContext _context;
        private readonly IOrganizationServiceFactory _serviceFactory;
        private readonly ITracingService _tracingService;

        public PluginExecutionServiceProvider(IPluginExecutionContext context,IOrganizationServiceFactory serviceFactory)
        {
            _context = context;
            _serviceFactory = serviceFactory;
            _tracingService = new SilentTracingService();
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IPluginExecutionContext))
            {
                return _context;
            }

            if (serviceType == typeof(IOrganizationServiceFactory))
            {
                return _serviceFactory;
            }

            if (serviceType == typeof(ITracingService))
            {
                return _tracingService;
            }

            return null;
        }

        private sealed class SilentTracingService : ITracingService
        {
            public void Trace(string format, params object[] args)
            {
                // The copied plug-in obtains ITracingService but does not currently call Trace.
                // API execution status is persisted through mfd_pocopyprojectlog instead.
            }
        }
    }
}
