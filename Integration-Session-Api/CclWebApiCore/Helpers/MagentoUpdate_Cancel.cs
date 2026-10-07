using CclCrmProxyCore.Helpers;
using CclWebApi.Models;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Web;

namespace CclWebApi.Helpers
{
    public class MagentoUpdate_Cancel : MagentoUpdate
    {
        public const string CancelledStatus = "Cancelled";
        public override bool Update(UploadResult _result)
        {
            int? localTransType;
            int? localRegStatus;
            string localErrorMessage;
            msevtmgt_SessionRegistration localCRMSessionReg;
            SessionRegistrationResult localSesRegResult = SessionRegistrationResult.CreateAndInitializeFrom(this.SessionTrack);

            localCRMSessionReg = ServiceContext.msevtmgt_SessionRegistrationSet.Where(x => x.msevtmgt_Name == SessionTrack.SessionRegistration.RegistrationId).FirstOrDefault();
            if (localCRMSessionReg == null)
            {
                localErrorMessage = $"Session registration with id {SessionTrack.SessionRegistration.RegistrationId} for {SessionTrack.Contact.FirstName} {SessionTrack.Contact.LastName} was not found. Update aborted.";
                localSesRegResult.Message = localErrorMessage;
                _result.ProcessingErrors.Add(localErrorMessage);
                _result.SessionRegistrationResults.Add(localSesRegResult);
                return false;
            }

            localCRMSessionReg.ccl_magentochangereason = this.SessionTrack.SessionRegistration.ChangeReason;
            localCRMSessionReg.ccl_magentoorderid = this.SessionTrack.SessionRegistration.OrderId;
            localTransType = CRMHelper.GetOptionSetValue(this.Service, localCRMSessionReg.LogicalName, "ccl_magentotransactiontype", this.SessionTrack.SessionRegistration.TransactionType.ToString());
            localCRMSessionReg.ccl_magentotransactiontype = localTransType.HasValue ? new OptionSetValue(localTransType.Value) : null;
            localRegStatus = CRMHelper.GetOptionSetValue(this.Service, localCRMSessionReg.LogicalName, "ccl_registrationstatus", MagentoUpdate_Cancel.CancelledStatus);
            localCRMSessionReg.ccl_registrationstatus = localRegStatus.HasValue ? new OptionSetValue(localRegStatus.Value) : null;
            localCRMSessionReg.ccl_agentid = this.SessionTrack.SessionRegistration.AgentId;
            this.ServiceContext.UpdateObject(localCRMSessionReg);
            this.ServiceContext.SaveChanges();

            localSesRegResult.Message = SessionRegistrationResult.Success;
            _result.SessionRegistrationResults.Add(localSesRegResult);
            _result.ProcessedOK.Add(SessionTrack.RecordNo.ToString());

            return true;
        }
    }
}