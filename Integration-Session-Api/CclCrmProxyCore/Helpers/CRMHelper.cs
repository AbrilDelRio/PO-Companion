using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace CclCrmProxyCore.Helpers
{
    public class CRMHelper
    {
        public const string CancelledRegistrationStatus = "cancelled";
        public const string PendingRegistrationStatus = "pending";
        public const string TenativeRegistartionStatus = "tentative";
        public const string ConfirmedRegistrationStatus = "confirmed";


        //public static IOrganizationService GetGlobalCrmService()
        //{
        //    var creds = new System.ServiceModel.Description.ClientCredentials();
        //    creds.UserName.UserName = "CRMIntegrationAPI@ccl.org";
        //    creds.UserName.Password = "4cRm@cCl";
        //    Uri appReplyUri = new Uri("app://51f81489-12ee-4a9e-aaae-a2591f45987d");
        //    Uri discoUrl = new Uri("https://globaldisco.crm.dynamics.com/api/discovery/v2.0/Instances");
        //    CrmServiceClient.
        //}
        // Returns null when a CRM connection cannot be established. Callers rely on this null
        // contract (e.g. SessionController.GetContactInfo does `if (svc != null)`), so this must NOT
        // throw. It previously swallowed the failure silently, which resurfaced two layers away as an
        // opaque ArgumentNullException(Parameter 'service') in CclServiceContext with no cause. It now
        // logs WHY via Serilog's process-wide static logger (wired to InsightOps/Console in Program.cs;
        // Error clears the MinimumLevel.Default:Error threshold) before returning null.
        public static IOrganizationService GetCrmService(string connString)
        {
            if (string.IsNullOrWhiteSpace(connString))
            {
                Serilog.Log.Error("GetCrmService: CRM connection string (ConnectionStrings:CRMConnection) is missing or empty.");
                return null;
            }

            ServiceClient conn;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                conn = new ServiceClient(connString);
            }
            catch (Exception e)
            {
                Serilog.Log.Error(e, "GetCrmService: failed to construct CRM ServiceClient.");
                return null;
            }

            // ServiceClient does NOT throw on auth/connection failure — it returns a client with
            // IsReady == false and the reason on LastError/LastException. Log that too, otherwise a
            // dead client flows downstream and blows up later with no explanation.
            if (!conn.IsReady)
            {
                Serilog.Log.Error(conn.LastException,
                    "GetCrmService: CRM ServiceClient is not ready (authentication/connection failed). LastError: {LastError}",
                    conn.LastError);
                return null;
            }

            // CrmServiceClient (conn) implements IOrganizationService directly.
            // See https://docs.microsoft.com/en-us/powerapps/developer/data-platform/authenticate-office365-deprecation
            return (IOrganizationService)conn;
        }

        public static void DeactivateRecord(EntityReference recordToDeactivate, int recordInactiveState, IOrganizationService service)
        {
            SetStateRequest ssr = new SetStateRequest
            {
                EntityMoniker = recordToDeactivate,
                State = new Microsoft.Xrm.Sdk.OptionSetValue(recordInactiveState),
                Status = new Microsoft.Xrm.Sdk.OptionSetValue(2)
            };

            service.Execute(ssr);
        }

        public static void ActivateRecord(EntityReference recordToActivate, int recordActiveState, IOrganizationService service)
        {
            SetStateRequest ssr = new SetStateRequest
            {
                EntityMoniker = recordToActivate,
                State = new Microsoft.Xrm.Sdk.OptionSetValue(recordActiveState),
                Status = new Microsoft.Xrm.Sdk.OptionSetValue(1)
            };

            service.Execute(ssr);
        }

        public static string GetOptionSetTextFromValue(IOrganizationService service,string entityName, string attributeName, int optionSetValue)
        {

            var attReq = new RetrieveAttributeRequest();

            attReq.EntityLogicalName = entityName;

            attReq.LogicalName = attributeName;

            attReq.RetrieveAsIfPublished = true;

            var attResponse = service.Execute(attReq) as RetrieveAttributeResponse;

            var attMetadata = (EnumAttributeMetadata)attResponse.AttributeMetadata;

            return attMetadata.OptionSet.Options.Where(x => x.Value == optionSetValue).FirstOrDefault().Label.UserLocalizedLabel.Label;

        }

        public static int? GetOptionSetValue(IOrganizationService service, string entityName, string attributeName, string attributeText)
        {
            int? ret = null;
            try
            {
                var attributeRequest = new RetrieveAttributeRequest
                {
                    EntityLogicalName = entityName,
                    LogicalName = attributeName,
                    RetrieveAsIfPublished = true
                };

                var attributeResponse = (RetrieveAttributeResponse)service.Execute(attributeRequest);
                var attributeMetadata = (EnumAttributeMetadata)attributeResponse.AttributeMetadata;

                var optionList = (from o in attributeMetadata.OptionSet.Options
                                  select new { Value = o.Value, Text = o.Label.UserLocalizedLabel.Label }).ToList();

                var res = optionList.Where(x => x.Text.Equals(attributeText,StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                if (res != null)
                    ret = res.Value;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error in GetOptionSetValue for entity '{entityName}' and field '{attributeName}'. Details: {ex.Message}");
            }

            return ret;
        }

        public static List<CclCrmProxyCore.Helpers.msevtmgt_SessionRegistration> getSessionRegistrationsInTrack(IOrganizationService _service, string _sessionTrackId,Guid _contactId)
        {
            List<CclCrmProxyCore.Helpers.msevtmgt_SessionRegistration> localList;

            var fetchXml = $@"<fetch>
                              <entity name='msevtmgt_sessiontrack'>
                                <filter type='and'>
                                  <filter type='and'>
                                    <condition attribute='ccl_sessiontrackid' operator='eq' value='{_sessionTrackId}'/>
                                  </filter>
                                </filter>
                                <link-entity name='msevtmgt_session' from='ccl_sessiontrack' to='msevtmgt_sessiontrackid' alias='Session'>
                                  <link-entity name='msevtmgt_sessionregistration' from='msevtmgt_sessionid' to='msevtmgt_sessionid' link-type='inner' alias='SessionRegistration'>
                                    <attribute name='msevtmgt_sessionregistrationid' />
                                    <filter type='and'>
                                      <condition attribute='msevtmgt_contactid' operator='eq' value='{_contactId.ToString()}'/>
                                    </filter>
                                  </link-entity>
                                </link-entity>
                              </entity>
                            </fetch>";

            EntityCollection entityRecords = _service.RetrieveMultiple(new FetchExpression(fetchXml));

            localList = new List<CclCrmProxyCore.Helpers.msevtmgt_SessionRegistration>(entityRecords.Entities.Count);

            foreach(var entity in entityRecords.Entities)
            {
                CclCrmProxyCore.Helpers.msevtmgt_SessionRegistration cclRegistration = new CclCrmProxyCore.Helpers.msevtmgt_SessionRegistration();
                cclRegistration.Id = (Guid)entity.GetAttributeValue<Microsoft.Xrm.Sdk.AliasedValue>("SessionRegistration.msevtmgt_sessionregistrationid").Value;
                localList.Add(cclRegistration);
            }

            return localList;

        }


    }
}