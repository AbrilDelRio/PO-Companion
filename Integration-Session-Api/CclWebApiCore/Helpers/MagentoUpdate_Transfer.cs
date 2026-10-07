using CclCrmProxyCore.Helpers;
using CclWebApi.Models;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Web;
using Microsoft.Xrm.Sdk.Messages;


namespace CclWebApi.Helpers
{
    public class MagentoUpdate_Transfer : MagentoUpdate
    {

        public override bool Validate()
        {
            if(SessionTrack ==null || SessionTrack.Session==null || (string.IsNullOrWhiteSpace(SessionTrack.Session.SessionID) && string.IsNullOrWhiteSpace(SessionTrack.Session.SessionGuid)))
            {
                throw new ValidationException("Session ID is missing");
            }

            return base.Validate();
        }

        public override bool Update(UploadResult _result)
        {
            int? localOptionSetValue;
            string localErrorMessage;
            msevtmgt_SessionRegistration localCRMSessionReg;
            msevtmgt_SessionRegistration localCRMSessionRegNew;
            CclCrmProxyCore.Helpers.Contact localCrmContact;
            List<Annotation> notesToTransfer;
            Annotation targetNote;
            bool changingSession;

            SessionRegistrationResult localSesRegResult = SessionRegistrationResult.CreateAndInitializeFrom(this.SessionTrack);

            msevtmgt_Session localCRMSession;

            if (string.IsNullOrWhiteSpace(SessionTrack.Session.SessionID))
            {
                localCRMSession = ServiceContext.msevtmgt_SessionSet.Where(x => x.msevtmgt_SessionId == Guid.Parse(SessionTrack.Session.SessionGuid)).FirstOrDefault();
            }
            else
            {
                localCRMSession = ServiceContext.msevtmgt_SessionSet.Where(x => x.new_SessionID == SessionTrack.Session.SessionID).FirstOrDefault();
            }

            if (localCRMSession == null)
            {
                var localSessionId = string.IsNullOrWhiteSpace(SessionTrack.Session.SessionID) ? SessionTrack.Session.SessionGuid : SessionTrack.Session.SessionID;
                localErrorMessage = $"Record #: {SessionTrack.RecordNo}, Can't find Session in D365 with ID {localSessionId}";
                localSesRegResult.Message = localErrorMessage;
                _result.SessionRegistrationResults.Add(localSesRegResult);
                return false;
            }

            localCRMSessionReg = ServiceContext.msevtmgt_SessionRegistrationSet.Where(x => x.msevtmgt_Name == SessionTrack.SessionRegistration.RegistrationId).FirstOrDefault();
            if (localCRMSessionReg == null)
            {
                localErrorMessage = $"Session registration with id {SessionTrack.SessionRegistration.RegistrationId} for {SessionTrack.Contact.FirstName} {SessionTrack.Contact.LastName} was not found. Update aborted.";
                localSesRegResult.Message = localErrorMessage;              
                _result.ProcessingErrors.Add(localErrorMessage);
                _result.SessionRegistrationResults.Add(localSesRegResult);
                return false;
            }

            changingSession = !string.Equals(localCRMSessionReg.msevtmgt_SessionId.Id.ToString(), localCRMSession.Id.ToString(), StringComparison.InvariantCultureIgnoreCase);

            if (changingSession)
            {
                localCRMSessionRegNew = this.initNewSessionRegistration(localCRMSessionReg, localCRMSession);
                localOptionSetValue = CRMHelper.GetOptionSetValue(this.Service, localCRMSessionRegNew.LogicalName, "ccl_registrationstatus", this.SessionTrack.SessionRegistration.RegistrationStatus);
                localCRMSessionRegNew.ccl_registrationstatus = localOptionSetValue.HasValue ? new OptionSetValue(localOptionSetValue.Value) : null;
                localCRMSessionRegNew.ccl_magentoorderid = this.SessionTrack.SessionRegistration.OrderId;
                localOptionSetValue = CRMHelper.GetOptionSetValue(this.Service, localCRMSessionRegNew.LogicalName, "ccl_magentotransactiontype", this.SessionTrack.SessionRegistration.TransactionType.ToString());
                localCRMSessionRegNew.ccl_magentotransactiontype = localOptionSetValue.HasValue ? new OptionSetValue(localOptionSetValue.Value) : null;
                localCRMSessionRegNew.ccl_magentochangereason = this.SessionTrack.SessionRegistration.ChangeReason;
                localCRMSessionRegNew.ccl_agentid = this.SessionTrack.SessionRegistration.AgentId;
                try
                {
                    this.ServiceContext.AddObject(localCRMSessionRegNew);
                    this.ServiceContext.SaveChanges();


                    var retrievedEntity = new Entity("msevtmgt_sessionregistration")
                    {
                        Id = localCRMSessionReg.Id,
                    };

                    notesToTransfer = this.getNotesForObject(localCRMSessionReg.Id);
                    if (notesToTransfer != null && notesToTransfer.Count>0)
                    {
                        foreach(Annotation sourceNote in notesToTransfer)
                        {
                            targetNote = new Annotation();
                            targetNote.Subject = sourceNote.Subject;
                            targetNote.NoteText = sourceNote.NoteText;
                            targetNote.ObjectTypeCode = sourceNote.ObjectTypeCode;
                            targetNote.ObjectId = localCRMSessionRegNew.ToEntityReference();
                            this.ServiceContext.AddObject(targetNote);
                            this.ServiceContext.SaveChanges();
                        }
                    }

                    var request = new DeleteRequest()
                    {
                        Target = retrievedEntity.ToEntityReference(),
                        ConcurrencyBehavior = ConcurrencyBehavior.AlwaysOverwrite
                    };
                    this.Service.Execute(request);

                    localSesRegResult.RegistrationId = localCRMSessionRegNew.msevtmgt_Name;
                }
                catch (Exception _error)
                {
                    if (localCRMSessionRegNew.Id != null && localCRMSessionRegNew.Id != Guid.Empty)
                    {
                        this.ServiceContext.AddObject(localCRMSessionRegNew);
                        this.ServiceContext.DeleteObject(localCRMSessionRegNew);
                    }

                    _result.ProcessingErrors.Add(_error.Message);
                    _result.SessionRegistrationResults.Add(localSesRegResult);
                    return false;
                }
            }
            else
            {
                localOptionSetValue = CRMHelper.GetOptionSetValue(this.Service, localCRMSessionReg.LogicalName, "ccl_registrationstatus", this.SessionTrack.SessionRegistration.RegistrationStatus);
                localCRMSessionReg.ccl_registrationstatus = localOptionSetValue.HasValue ? new OptionSetValue(localOptionSetValue.Value) : null;
                localCRMSessionReg.ccl_magentochangereason = this.SessionTrack.SessionRegistration.ChangeReason;
                localCRMSessionReg.ccl_magentoorderid = this.SessionTrack.SessionRegistration.OrderId;
                localOptionSetValue = CRMHelper.GetOptionSetValue(this.Service, localCRMSessionReg.LogicalName, "ccl_magentotransactiontype", this.SessionTrack.SessionRegistration.TransactionType.ToString());
                localCRMSessionReg.ccl_magentotransactiontype = localOptionSetValue.HasValue ? new OptionSetValue(localOptionSetValue.Value) : null;
                localCRMSessionReg.ccl_agentid = this.SessionTrack.SessionRegistration.AgentId;
                this.ServiceContext.UpdateObject(localCRMSessionReg);
                this.ServiceContext.SaveChanges();
            }

            localCrmContact = this.ServiceContext.ContactSet.Where(x => x.ContactId == localCRMSessionReg.msevtmgt_ContactId.Id).OrderBy(x => x.StateCode).ThenByDescending(x => x.ModifiedOn).FirstOrDefault(); //take most-recent one (firstly Active ones)


            if (localCrmContact != null)
            {
                CrmWorker.MapContact(this.SessionTrack, localCrmContact,this.ServiceContext);
                this.ServiceContext.UpdateObject(localCrmContact);
                this.ServiceContext.SaveChanges();
            }


            localSesRegResult.Message = SessionRegistrationResult.Success;
            
            _result.SessionRegistrationResults.Add(localSesRegResult);
            _result.ProcessedOK.Add(this.SessionTrack.RecordNo.ToString());

            return true;
        }

        protected msevtmgt_SessionRegistration initNewSessionRegistration(msevtmgt_SessionRegistration _sourceRegistration, msevtmgt_Session _targetSession)
        {

            msevtmgt_SessionRegistration localNewCrmSessionRegistration = new msevtmgt_SessionRegistration();
            localNewCrmSessionRegistration.ccl_magentochangereason = _sourceRegistration.ccl_magentochangereason;
            localNewCrmSessionRegistration.ccl_magentoorderid = _sourceRegistration.ccl_magentoorderid;
            localNewCrmSessionRegistration.ccl_magentotransactiontype = _sourceRegistration.ccl_magentotransactiontype;
            localNewCrmSessionRegistration.ccl_registrationstatus = _sourceRegistration.ccl_registrationstatus;
            localNewCrmSessionRegistration.ccl_scholarshiprecipient = _sourceRegistration.ccl_scholarshiprecipient;
            localNewCrmSessionRegistration.ccl_sessiontransferred = true;
            localNewCrmSessionRegistration.ccl_agentid = _sourceRegistration.ccl_agentid;
            localNewCrmSessionRegistration.msevtmgt_ContactId = _sourceRegistration.msevtmgt_ContactId;
            localNewCrmSessionRegistration.msevtmgt_Event = _targetSession.msevtmgt_Event;
            localNewCrmSessionRegistration.msevtmgt_Name = _sourceRegistration.msevtmgt_Name;
            localNewCrmSessionRegistration.msevtmgt_publishingstate = _sourceRegistration.msevtmgt_publishingstate;
            localNewCrmSessionRegistration.msevtmgt_registrationstatus = _sourceRegistration.msevtmgt_registrationstatus;
            localNewCrmSessionRegistration.msevtmgt_SessionId = _targetSession.ToEntityReference();
            localNewCrmSessionRegistration.OwnerId = _sourceRegistration.OwnerId;
            localNewCrmSessionRegistration.msevtmgt_SyncedWithProvider = _sourceRegistration.msevtmgt_SyncedWithProvider;
            localNewCrmSessionRegistration.msevtmgt_registrationnotificationseen = _sourceRegistration.msevtmgt_registrationnotificationseen;
            localNewCrmSessionRegistration.msevtmgt_createdFromApi = _sourceRegistration.msevtmgt_createdFromApi;

            return localNewCrmSessionRegistration;
        }
    }
}