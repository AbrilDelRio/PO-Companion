
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
    public class CreateSessionRegistration
    {
        IOrganizationService service;

        public CreateSessionRegistration(IOrganizationService _service)
        {
            service = _service;
        }

        public bool Run(CclServiceContext _cclContext, SessionTrack _st, CclCrmProxyCore.Helpers.Contact _crmContact, UploadResult _uplResults)
        {
            int? localTransType;
            int? localRegStatus;
            SessionRegistrationResult localSesRegResult;
            msevtmgt_Session session;
            bool canCreate;

            if (string.IsNullOrWhiteSpace(_st.Session.SessionID))
            {
                session = _cclContext.msevtmgt_SessionSet.Where(x => x.msevtmgt_SessionId == Guid.Parse(_st.Session.SessionGuid)).FirstOrDefault();
            }
            else
            {
                session = _cclContext.msevtmgt_SessionSet.Where(x => x.new_SessionID == _st.Session.SessionID).FirstOrDefault();
            }

            if (session == null)
            {
                var localSessionId = string.IsNullOrWhiteSpace(_st.Session.SessionID) ? _st.Session.SessionGuid : _st.Session.SessionID;
                throw new ValidationException($"Record #: {_st.RecordNo}, Can't find Session in D365 with ID {localSessionId }");
            }


            var srExists = _cclContext.msevtmgt_SessionRegistrationSet.Where(x => x.msevtmgt_SessionId == session.ToEntityReference()
                && x.msevtmgt_ContactId == _crmContact.ToEntityReference()).OrderByDescending(y => y.CreatedOn).FirstOrDefault();


            if(srExists ==null)
            {
                canCreate = true;
            }
            else
            {
                string registrationStatus = CRMHelper.GetOptionSetTextFromValue(service, srExists.LogicalName, "ccl_registrationstatus", srExists.ccl_registrationstatus.Value).ToLower();
                switch(registrationStatus)
                {
                    case CRMHelper.CancelledRegistrationStatus:
                        canCreate = true;
                        break;
                    case CRMHelper.PendingRegistrationStatus:
                    case CRMHelper.TenativeRegistartionStatus:
                        return new UpdateSessionRegistration(this.service).Run(_cclContext, _st, srExists,_uplResults);
                    default:
                        canCreate = false;
                        break;
                }
            }


            if (!canCreate)
            {
                _uplResults.ProcessingErrors.Add($"Session registration {srExists?.msevtmgt_Name} already exists for {_crmContact.FirstName} {_crmContact.LastName} ({_crmContact.EMailAddress1})");
                return false;
            }


            msevtmgt_SessionRegistration sesReg = new msevtmgt_SessionRegistration();

            localRegStatus = CRMHelper.GetOptionSetValue(this.service, sesReg.LogicalName, "ccl_registrationstatus", string.IsNullOrEmpty(_st.SessionRegistration.RegistrationStatus) ? "Tentative" : _st.SessionRegistration.RegistrationStatus);

            sesReg.msevtmgt_SessionId = session.ToEntityReference();
            sesReg.msevtmgt_ContactId = _crmContact.ToEntityReference();

            sesReg.ccl_registrationstatus = localRegStatus.HasValue ? new OptionSetValue(localRegStatus.Value) : null;

            sesReg.msevtmgt_Event = session.msevtmgt_Event;
            sesReg.ccl_magentoorderid = _st.SessionRegistration.OrderId;
            sesReg.ccl_agentid = _st.SessionRegistration.AgentId;
            localTransType = CRMHelper.GetOptionSetValue(service, sesReg.LogicalName, "ccl_magentotransactiontype", _st.SessionRegistration.TransactionType.ToString());
            sesReg.ccl_magentotransactiontype = localTransType.HasValue ? new OptionSetValue(localTransType.Value) : null;

            _cclContext.AddObject(sesReg);
            _cclContext.SaveChanges();

            if (_st.RecordSource == RecordSource.Magento && _st.SessionRegistration.TransactionType == MagentoTransactionType.New)
            {
                localSesRegResult = new SessionRegistrationResult();

                var fetchXml = $@"<fetch>
                                  <entity name='msevtmgt_sessionregistration'>
                                    <attribute name='msevtmgt_name'/>
                                    <filter type='and'>
                                      <condition attribute='msevtmgt_sessionregistrationid' operator='eq' value='{sesReg.Id.ToString()}'/>
                                    </filter>
                                  </entity>
                                </fetch>";
                var savedSesReg = service.RetrieveMultiple(new FetchExpression(fetchXml)).Entities.FirstOrDefault();
                if (savedSesReg != null && savedSesReg.Contains("msevtmgt_name"))
                {
                    localSesRegResult.RegistrationId = savedSesReg["msevtmgt_name"].ToString();
                }

                localSesRegResult.OrderId = _st.SessionRegistration.OrderId;
                localSesRegResult.SessionTrackId = _st.SessionTrackID;
                localSesRegResult.SessionId = session.new_SessionID;
                _uplResults.SessionRegistrationResults.Add(localSesRegResult);
            }

            Annotation note = new Annotation();
            note.Subject = _st.SessionRegistration.NoteTitle;
            note.NoteText = _st.SessionRegistration.Note;
            note.ObjectId = sesReg.ToEntityReference();
            note.ObjectTypeCode = msevtmgt_SessionRegistration.EntityLogicalName;

            _cclContext.AddObject(note);
            _cclContext.SaveChanges();

            return true;
        }
    }
}