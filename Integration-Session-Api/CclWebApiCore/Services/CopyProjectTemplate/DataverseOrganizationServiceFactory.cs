using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;

namespace CclWebApi.Services.CopyProjectTemplate
{
    /// <summary>
    /// Reuses the API's authenticated Dataverse ServiceClient and, when a user id is supplied,
    /// executes the copy on behalf of that Dataverse user through ServiceClient.CallerId.
    /// </summary>
    internal sealed class DataverseOrganizationServiceFactory : IOrganizationServiceFactory
    {
        private readonly IOrganizationService _service;

        public DataverseOrganizationServiceFactory(IOrganizationService service)
        {
            _service = service;
        }

        /// <summary>
        /// INVARIANT: this mutates CallerId on the ServiceClient it was handed and never resets it,
        /// so it is only safe because BaseCrmController builds a fresh ServiceClient per request
        /// (CRMHelper.GetCrmService -> new ServiceClient(connString)). If that connection is ever
        /// cached, pooled or registered as a singleton, impersonation will leak across requests and
        /// unrelated callers will start executing as whoever last posted a userId. Set CallerId on
        /// a Clone() instead if that day comes.
        /// </summary>
        public IOrganizationService CreateOrganizationService(Guid? userId)
        {
            if (!userId.HasValue || userId.Value == Guid.Empty)
            {
                return _service;
            }

            if (_service is not ServiceClient serviceClient)
            {
                throw new InvalidOperationException(
                    "CopyProjectTemplate cannot impersonate the requested Dataverse user because the API CRM service is not a ServiceClient.");
            }

            serviceClient.CallerId = userId.Value;
            return serviceClient;
        }
    }
}
