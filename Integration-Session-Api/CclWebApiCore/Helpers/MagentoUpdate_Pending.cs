using CclWebApi.Models;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Web;
using CclCrmProxyCore.Helpers;


namespace CclWebApi.Helpers
{
    public class MagentoUpdate_Pending : MagentoUpdate
    {
        public override bool Update(UploadResult _result)
        {
            string localErrorMessage;
            msevtmgt_SessionRegistration localCRMSessionReg;
            SessionRegistrationResult localSesRegResult = SessionRegistrationResult.CreateAndInitializeFrom(this.SessionTrack);
            bool outcome = true;

            localCRMSessionReg = ServiceContext.msevtmgt_SessionRegistrationSet.Where(x => x.msevtmgt_Name == SessionTrack.SessionRegistration.RegistrationId).FirstOrDefault();

            if (localCRMSessionReg == null)
            {
                localErrorMessage = $"Session registration with id {SessionTrack.SessionRegistration.RegistrationId} for {SessionTrack.Contact.FirstName} {SessionTrack.Contact.LastName} was not found. Update aborted.";
                localSesRegResult.Message = localErrorMessage;
                _result.ProcessingErrors.Add(localErrorMessage);
                _result.SessionRegistrationResults.Add(localSesRegResult);
                outcome = false;
            }
            else
            {
                string preRegStatusText = CRMHelper.GetOptionSetTextFromValue(this.Service, localCRMSessionReg.LogicalName, "ccl_registrationstatus", localCRMSessionReg.ccl_registrationstatus.Value).ToLower();

                switch (preRegStatusText)
                {
                    case CRMHelper.CancelledRegistrationStatus:
                        var crmContact = ServiceContext.ContactSet.Where(x => x.Id == localCRMSessionReg.msevtmgt_ContactId.Id).FirstOrDefault();
                        outcome = new CreateSessionRegistration(this.Service).Run(ServiceContext, this.SessionTrack, crmContact, _result);
                        break;
                    case CRMHelper.PendingRegistrationStatus:
                    case CRMHelper.TenativeRegistartionStatus:
                    case CRMHelper.ConfirmedRegistrationStatus:
                        outcome = new UpdateSessionRegistration(this.Service).Run(ServiceContext, this.SessionTrack, localCRMSessionReg,_result);
                        break;
                    default:
                        localErrorMessage = $"Cannot update a session registrations for {SessionTrack.Contact.FirstName} {SessionTrack.Contact.LastName} because it is in {preRegStatusText} status";
                        localSesRegResult.Message = localErrorMessage;
                        _result.SessionRegistrationResults.Add(localSesRegResult);
                        _result.ProcessingErrors.Add(localErrorMessage);
                        outcome = false;
                        break;
                }
            }

            return outcome;
        }
    }
}