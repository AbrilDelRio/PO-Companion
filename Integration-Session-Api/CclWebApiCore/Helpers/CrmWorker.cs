using CclCrmProxyCore.Helpers;
using CclWebApi.Models;
using Microsoft.Xrm.Sdk;
using System.Runtime.Serialization.Json;

namespace CclWebApi.Helpers
{

    public class CrmWorker
    {
        IOrganizationService service;

        IConfiguration _configuration;

        public CrmWorker(IConfiguration configuration)
        {
            _configuration = configuration;
            service = CRMHelper.GetCrmService(Helper.GetCrmConnString(_configuration));
            
        }

        public UploadResult ProcessSessionTracks(List<SessionTrack> stList)
        {
            MagentoUpdate localMagentoUpdate;

            if (stList == null || stList.Count < 1)
                throw new Exception("No Session Tracks found");

            UploadResult uplResults = new UploadResult();

            using (var cclContext = new CclServiceContext(service))
            {
                foreach (var st in stList)
                {
                    if (!ValidateLine(st, uplResults.ProcessingErrors))
                    {
                        continue;
                    }

                    try
                    {
                        localMagentoUpdate = MagentoUpdate.construct(service, st);
                        if (localMagentoUpdate is null)
                        {
                            #region Contact
                            var crmContact = cclContext.ContactSet.Where(x => x.EMailAddress1 == st.Contact.Email).OrderBy(x => x.StateCode).ThenByDescending(x => x.ModifiedOn).FirstOrDefault(); //take most-recent one (firstly Active ones)
                            if (crmContact == null)
                            {
                                crmContact = new CclCrmProxyCore.Helpers.Contact();
                                CrmWorker.MapContact(st, crmContact,cclContext);
                                cclContext.AddObject(crmContact);
                                cclContext.SaveChanges();
                            }
                            else //Existing Contact
                            {
                                CrmWorker.MapContact(st, crmContact,cclContext);
                                cclContext.UpdateObject(crmContact);
                                cclContext.SaveChanges();

                                if (crmContact.StateCode == ContactState.Inactive)
                                    CRMHelper.ActivateRecord(crmContact.ToEntityReference(), (int)ContactState.Active, service);
                            }
                            #endregion

                            #region Session
                            if (st.Session.hasSessionId()) //Session populated, create only SRs
                            {
                                if(new CreateSessionRegistration(service).Run(cclContext,st,crmContact,uplResults))
                                {
                                    uplResults.ProcessedOK.Add(st.RecordNo.ToString());
                                }
                            }
                            else if (st.hasSessionTrackId())
                            {
                                if (CreateSessionTrackRegistration(cclContext, st, crmContact, uplResults))
                                {
                                    uplResults.ProcessedOK.Add(st.RecordNo.ToString());
                                }
                            }
                        }
                        else
                        {
                            if (localMagentoUpdate.Validate())
                            {
                                localMagentoUpdate.Update(uplResults);
                            }
                        }
                        #endregion
                    }
                    catch (ValidationException vex)
                    {
                        uplResults.ProcessingErrors.Add(vex.Message);
                        continue;
                    }
                }
            }

            return uplResults;
        }

        private bool CreateSessionTrackRegistration(CclServiceContext cclContext, SessionTrack st, CclCrmProxyCore.Helpers.Contact crmContact, UploadResult uplResults)
        {
            List<msevtmgt_SessionRegistration> trackSessionsRegistrations;
            msevtmgt_SessionTrack sesTrack;

            if (string.IsNullOrWhiteSpace(st.SessionTrackGuid))
            {
                sesTrack = cclContext.msevtmgt_SessionTrackSet.Where(x => x.ccl_sessiontrackid == st.SessionTrackID).FirstOrDefault();
            }
            else
            {
                sesTrack = cclContext.msevtmgt_SessionTrackSet.Where(x => x.msevtmgt_SessionTrackId == Guid.Parse(st.SessionTrackGuid)).FirstOrDefault();
            }

            if (sesTrack == null)
            {
                var localSessionTrackId = string.IsNullOrWhiteSpace(st.SessionTrackID) ? st.SessionTrackGuid : st.SessionTrackID;

                throw new ValidationException($"Record #: {st.RecordNo}, Can't find Session Track in D365 with ID {localSessionTrackId}");
            }

            var srExists = cclContext.ccl_sessiontrackregistrationSet.Where(x => x.ccl_SessionTrack == sesTrack.ToEntityReference()
                && x.ccl_contact == crmContact.ToEntityReference()).FirstOrDefault();
            if (srExists == null)
            {
                ccl_sessiontrackregistration stReg = new ccl_sessiontrackregistration();
                stReg.ccl_name = crmContact.FullName;
                stReg.ccl_SessionTrack = sesTrack.ToEntityReference();
                stReg.ccl_contact = crmContact.ToEntityReference();
                stReg.ccl_contactregistrationinfos = this.getContactRegistrationInfos(st);
                //stReg.ccl_event = sesTrack.msevtmgt_EventId;
                cclContext.AddObject(stReg);
                cclContext.SaveChanges();

                Annotation note = new Annotation();
                note.Subject = st.SessionRegistration.NoteTitle;
                note.NoteText = st.SessionRegistration.Note;
                note.ObjectId = stReg.ToEntityReference();
                note.ObjectTypeCode = ccl_sessiontrackregistration.EntityLogicalName;

                cclContext.AddObject(note);

                trackSessionsRegistrations = CRMHelper.getSessionRegistrationsInTrack(service, sesTrack.ccl_sessiontrackid, crmContact.ToEntityReference().Id);
                for (int x = 0; x < trackSessionsRegistrations.Count; x++)
                {
                    note = new Annotation();
                    note.Subject = st.SessionRegistration.NoteTitle;
                    note.NoteText = st.SessionRegistration.Note;
                    note.ObjectId = trackSessionsRegistrations[x].ToEntityReference();
                    note.ObjectTypeCode = msevtmgt_SessionRegistration.EntityLogicalName;
                    cclContext.AddObject(note);
                }


                cclContext.SaveChanges();
            }
            else
            {
                srExists.ccl_contactregistrationinfos = this.getContactRegistrationInfos(st);
                cclContext.UpdateObject(srExists);
                cclContext.SaveChanges();
            }

            return true;

        }

        private string getContactRegistrationInfos(SessionTrack _sessionTrack)
        {
            
            using (var localMemoryStream = new MemoryStream())
            {
                DataContractJsonSerializer localSerializer = new DataContractJsonSerializer(typeof(SessionRegistration));
                localSerializer.WriteObject(localMemoryStream, _sessionTrack.SessionRegistration);
                localMemoryStream.Position = 0;
                StreamReader streamReader = new StreamReader(localMemoryStream);
                return streamReader.ReadToEnd();
            }
        }
            

        private bool ValidateLine(SessionTrack st, List<string> processingErrors)
        {
            if(st.SessionRegistration.TransactionType == MagentoTransactionType.New && !st.Session.hasSessionId() && !st.hasSessionTrackId())
            {
                processingErrors.Add($"$Record #: {st.RecordNo}, Session ID is missing");
                return false;
            }

            if (string.IsNullOrWhiteSpace(st.Contact.Email))
            {
                processingErrors.Add($"Record #: {st.RecordNo}, Contact does not have Email populated");
                return false;
            }

            if(!System.Text.RegularExpressions.Regex.IsMatch(st.Contact.Email, "^[0-9a-zA-Z']+([0-9a-zA-Z']*[-._+])*[0-9a-zA-Z']+@[0-9a-zA-Z]+([-.][0-9a-zA-Z]+)*([0-9a-zA-Z]*[.])[a-zA-Z]{2,12}$"))
            {
                processingErrors.Add($"Record #: {st.RecordNo} has email address {st.Contact.Email} which does not appear valid");
                return false;
            }

            if (st.Session.hasSessionId() && st.hasSessionTrackId())
            {
                processingErrors.Add($"Record #: {st.RecordNo}, Both SessionID and SessionTrackID fields are populated");
                return false;
            }

            if(string.IsNullOrWhiteSpace(st.Contact.FirstName))
            {
                processingErrors.Add($"Record #: {st.RecordNo}, Contact does not have Firstname populated.");
                return false;
            }

            if(string.IsNullOrWhiteSpace(st.Contact.LastName))
            {
                processingErrors.Add($"Record #: {st.RecordNo}, Contact does not have Lastname populated");
                return false;
            }

            if(string.IsNullOrWhiteSpace(st.Contact.CompanyName) && st.RecordSource != RecordSource.Magento)
            {
                processingErrors.Add($"Record #: {st.RecordNo}, Contact does not have Company Name populated");
                return false;
            }

            if(string.IsNullOrWhiteSpace(st.Contact.AddressLine))
            {
                processingErrors.Add($"Record #: {st.RecordNo}, Contact does not have Address Line populated");
                return false;
            }

            if(string.IsNullOrWhiteSpace(st.Contact.AddressCity))
            {
                processingErrors.Add($"Record #: {st.RecordNo}, Contact does not have Address City populated");
                return false;
            }

            if(string.IsNullOrWhiteSpace(st.Contact.AddressCountry))
            {
                processingErrors.Add($"Record #: {st.RecordNo}, Contact does not have Address Country populated");
                return false;
            }

            return true;
        }

        public static void MapContact(SessionTrack st, CclCrmProxyCore.Helpers.Contact crmContact,CclServiceContext _cclContext)
        {
            if (!string.IsNullOrEmpty(st.Contact.Email))
                crmContact.EMailAddress1 = st.Contact.Email;
            if (!string.IsNullOrEmpty(st.Contact.FirstName))
                crmContact.FirstName = st.Contact.FirstName;
            if (!string.IsNullOrEmpty(st.Contact.LastName))
                crmContact.LastName = st.Contact.LastName;
            if (!string.IsNullOrEmpty(st.Contact.MiddleName))
                crmContact.MiddleName = st.Contact.MiddleName;
            if (!string.IsNullOrEmpty(st.Contact.Salutation))
                crmContact.Salutation = st.Contact.Salutation;
            if (!string.IsNullOrEmpty(st.Contact.Suffix))
                crmContact.Suffix = st.Contact.Suffix;
            if (!string.IsNullOrEmpty(st.Contact.JobTitle))
                crmContact.JobTitle = st.Contact.JobTitle;
            if (!string.IsNullOrEmpty(st.Contact.CompanyName))
                crmContact.ccl_CompanyName = st.Contact.CompanyName;
            if (!string.IsNullOrEmpty(st.Contact.Telephone))
                crmContact.Telephone1 = st.Contact.Telephone;
            if (!string.IsNullOrEmpty(st.Contact.MobilePhone))
                crmContact.MobilePhone = st.Contact.MobilePhone;

            if(!string.IsNullOrEmpty(st.Contact.AddressLine))
            {
                crmContact.Address1_Line1 = st.Contact.AddressLine;
            }

            if(!string.IsNullOrEmpty(st.Contact.AddressCity))
            {
                crmContact.Address1_City = st.Contact.AddressCity;
            }

            if(!string.IsNullOrEmpty(st.Contact.AddressState))
            {
                crmContact.Address1_StateOrProvince = st.Contact.AddressState;
                var localCCLStateProvince = _cclContext.ccl_provincesSet.Where(x => x.ccl_name == st.Contact.AddressState || x.ccl_StateCode == st.Contact.AddressState).FirstOrDefault();
                if(localCCLStateProvince is null)
                {
                    crmContact.ccl_StateProvince = null;
                }
                else
                {
                    crmContact.ccl_StateProvince = localCCLStateProvince.ToEntityReference();
                }
            }

            if(!string.IsNullOrEmpty(st.Contact.AddressPostCode))
            {
                crmContact.Address1_PostalCode = st.Contact.AddressPostCode;
            }

            if(!string.IsNullOrEmpty(st.Contact.AddressCountry))
            {
                crmContact.Address1_Country = st.Contact.AddressCountry;
                var localCCLCountry = _cclContext.ccl_countrySet.Where(x => x.ccl_name == st.Contact.AddressCountry || x.ccl_CountryCode == st.Contact.AddressCountry).FirstOrDefault();
                if (localCCLCountry is null)
                {
                    crmContact.ccl_Country = null;
                }
                else
                {
                    crmContact.ccl_Country = localCCLCountry.ToEntityReference();
                }
            }
        }

        #region ProjectMilestone

        public UploadProjectMilestoneResult ProcessProjectMilestones(List<ProjectMilestoneResult> pmList)
        {
            //Update by remon 0727/2023
            if (pmList == null || pmList.Count < 1)
                throw new Exception("No CCL Project Contract Line Item Milestones found");

            UploadProjectMilestoneResult uploadPMResults = new UploadProjectMilestoneResult();

            using (var cclContext = new CclServiceContext(service))
            {
                foreach (var pm in pmList)
                {

                    try
                    {

                        var crmProjectMilestoneVerify = cclContext.new_projectcontractlinemilestoneverifySet.Where(x => x.new_referencenum == pm.ReferenceNum).FirstOrDefault(); //duplicate check 

                        if (crmProjectMilestoneVerify == null)
                        {
                            crmProjectMilestoneVerify = new CclCrmProxyCore.Helpers.new_projectcontractlinemilestoneverify();

                            if (!string.IsNullOrEmpty(pm.ReferenceNum))
                            {

                                crmProjectMilestoneVerify.new_referencenum = pm.ReferenceNum;

                                var parentProjectMilestoneId = cclContext.msdyn_cclcontractlinescheduleofvalueSet.Where(x => x.msdyn_cclcontractlinescheduleofvalueid == Guid.Parse(pm.ReferenceNum)).FirstOrDefault();
                                if (parentProjectMilestoneId is null)
                                {
                                    crmProjectMilestoneVerify.new_cclmatchedfinance = null;
                                }
                                else
                                {
                                    crmProjectMilestoneVerify.new_cclmatchedfinance = parentProjectMilestoneId.ToEntityReference();//parent project contract line milestone - match
                                }

                            }

                            if (!string.IsNullOrEmpty(pm.ProjectID))
                                crmProjectMilestoneVerify.new_projectid = pm.ProjectID;
                            if (!string.IsNullOrEmpty(pm.Description))
                                crmProjectMilestoneVerify.new_description = pm.Description;

                            cclContext.AddObject(crmProjectMilestoneVerify);
                            cclContext.SaveChanges();

                            uploadPMResults.ProcessedOK.Add(SessionRegistrationResult.Success);

                        }
                        else //existing record
                        {
                            uploadPMResults.ProcessingErrors.Add($"{SessionRegistrationResult.Failure} : Duplicate CCL Project Contract Line Milestone ReferenceNum {crmProjectMilestoneVerify.new_referencenum}");
                            continue;
                        }

                    }
                    catch (ValidationException pex)
                    {
                        uploadPMResults.ProcessingErrors.Add(pex.Message);
                        continue;
                    }
                }
            }

            return uploadPMResults;
            #endregion
        }
    }
}