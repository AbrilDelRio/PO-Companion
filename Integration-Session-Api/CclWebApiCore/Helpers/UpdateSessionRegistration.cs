using CclWebApi.Models;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Web;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Crm.Sdk.Messages;
using CclCrmProxyCore.Helpers;


namespace CclWebApi.Helpers
{
    public class UpdateSessionRegistration
    {
        IOrganizationService service;

        public UpdateSessionRegistration(IOrganizationService _service)
        {
            service = _service;
        }
        public bool Run(CclServiceContext _cclContext,SessionTrack _sessionTrack, msevtmgt_SessionRegistration _registration,UploadResult _uploadResult)
        {
            int? localTransType;
            int? localRegStatus;
            SessionRegistrationResult registrationResult;
            try
            {
                _registration.ccl_magentochangereason = _sessionTrack.SessionRegistration.ChangeReason;
                _registration.ccl_magentoorderid = _sessionTrack.SessionRegistration.OrderId;
                localTransType = CRMHelper.GetOptionSetValue(service, _registration.LogicalName, "ccl_magentotransactiontype", _sessionTrack.SessionRegistration.TransactionType.ToString());
                _registration.ccl_magentotransactiontype = localTransType.HasValue ? new OptionSetValue(localTransType.Value) : null;
                localRegStatus = CRMHelper.GetOptionSetValue(service, _registration.LogicalName, "ccl_registrationstatus", _sessionTrack.SessionRegistration.RegistrationStatus);
                _registration.ccl_registrationstatus = localRegStatus.HasValue ? new OptionSetValue(localRegStatus.Value) : _registration.ccl_registrationstatus;
                _registration.statecode = msevtmgt_SessionRegistrationState.Active; //SUN-018278 MSDYN Marketing deactivating cancelling session registrations - Disable this by force active status on all updates
                _registration.ccl_agentid = _sessionTrack.SessionRegistration.AgentId;
                _cclContext.UpdateObject(_registration);
                _cclContext.SaveChanges();
                registrationResult = new SessionRegistrationResult();
                registrationResult.RegistrationId = _registration.msevtmgt_Name;
                registrationResult.OrderId = _sessionTrack.SessionRegistration.OrderId;
                registrationResult.SessionTrackId = _sessionTrack.SessionTrackID;
                msevtmgt_Session session = _cclContext.msevtmgt_SessionSet.Where(x => x.Id == _registration.msevtmgt_SessionId.Id).FirstOrDefault();
                if (session != null)
                {
                    registrationResult.SessionId = session.new_SessionID;
                }
                registrationResult.Message = SessionRegistrationResult.Success;
                _uploadResult.SessionRegistrationResults.Add(registrationResult);
            }
            catch(Exception e)
            {
                throw new Exception($"{e.Message}. Failed to update existing registration");
            }

            return true;
        }
    }
}