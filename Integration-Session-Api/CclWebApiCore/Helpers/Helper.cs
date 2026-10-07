using System;
using System.Configuration;
using System.Net;
using Newtonsoft.Json;
using CclWebApi.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Crm.Sdk.Messages;

namespace CclWebApi.Helpers
{
    public class Helper
    {

        //public static readonly string SFClientSuffix = "_2";
        public const string SessionRegistrationReadOnlyEnforcementPlugin = "Microsoft.Dynamics.EventManagement.CrmPlugins.EventMgmtPlugin.Plugins.PreventModificationsOfReadOnlyFieldsPlugin: Update of msevtmgt_sessionregistration";

        public static string GetCrmConnString(IConfiguration configuration)
        {
            return configuration.GetConnectionString("CRMConnection");
        }

        public static DayOffParameters GetDayOffParemetersFromJson(string _json)
        {
            return string.IsNullOrWhiteSpace(_json) ? new DayOffParameters() : JsonConvert.DeserializeObject<DayOffParameters>(_json);
        }

        public static List<T> GetListFromJson<T>(string _json) where T : new()
        {
            return string.IsNullOrWhiteSpace(_json) ? new List<T>() : JsonConvert.DeserializeObject<List<T>>(_json);
        }

        public static T GetObjectFromJson<T>(string _json) where T:new()
        {
            return string.IsNullOrWhiteSpace(_json) ? new T() : JsonConvert.DeserializeObject<T>(_json);
        }

        public static string GetJsonFromList<T>(List<T> _list)
        {
            return _list == null || (_list != null && _list.Count == 0) ? string.Empty : JsonConvert.SerializeObject(_list, Formatting.None);
        }

        public static string GetJson<T>(T _object)
        {
            string localJson = string.Empty;

            if(_object != null)
            {
                JsonSerializerSettings localSettings = new JsonSerializerSettings();
                localSettings.Formatting = Formatting.None;
                localSettings.StringEscapeHandling = StringEscapeHandling.Default;
                localJson = JsonConvert.SerializeObject(_object, Formatting.None,localSettings);
            }

            return localJson;
        }

        public static void TogglePluginState(IOrganizationService _service,string _pluginName, bool _enabled)
        {
            var fetchXml = $@"<fetch>
                              <entity name='sdkmessageprocessingstep'>
                                <attribute name='name'/>
                                <attribute name='statuscode'/>
                                <attribute name='statecode'/>
                                <attribute name ='sdkmessageprocessingstepid'/>
                                <filter type='and'>
                                  <condition attribute='name' operator='eq' value='{_pluginName}'/>
                                </filter>
                              </entity>
                            </fetch>";

            var step = _service.RetrieveMultiple(new FetchExpression(fetchXml)).Entities.Where(x => x.Attributes["name"].ToString().Contains(_pluginName)).First();

            var pluginId = (Guid)step.Attributes["sdkmessageprocessingstepid"];

            int pluginStateCode = _enabled ? 0 : 1;

            int pluginStatusCode = _enabled ? 1 : 2;

            _service.Execute(new SetStateRequest
            {
                EntityMoniker = new EntityReference("sdkmessageprocessingstep", pluginId),
                State = new OptionSetValue(pluginStateCode),
                Status = new OptionSetValue(pluginStatusCode)
            });
        }

        public static List<SessionTrack> GetSessionTrackListFromSessionUploadInfoList(List<SessionUploadInfo> _sourceList)
        {
            List<SessionTrack> localTargetList = null;
            SessionTrack sessionTrack;
            MagentoTransactionType localMTransType;

            if (_sourceList != null && _sourceList.Count>0)
            {
                localTargetList = new List<SessionTrack>(_sourceList.Count);
                foreach (SessionUploadInfo suInfo in _sourceList)
                {
                    sessionTrack = new SessionTrack();
                    sessionTrack.SessionTrackID = suInfo.SessionTrackId;
                    sessionTrack.SessionTrackGuid = suInfo.SessionTrackGuid;
                    sessionTrack.Contact.FirstName = suInfo.ContactFirstName;
                    sessionTrack.Contact.MiddleName = suInfo.ContactMiddleName;
                    sessionTrack.Contact.LastName = suInfo.ContactLastName;
                    sessionTrack.Contact.Salutation = suInfo.ContactSalutation;
                    sessionTrack.Contact.Suffix = suInfo.ContactSuffix;
                    sessionTrack.Contact.JobTitle = suInfo.ContactJobTitle;
                    sessionTrack.Contact.CompanyName = suInfo.ContactCompanyName;
                    sessionTrack.Contact.Telephone = suInfo.ContactPhone;
                    sessionTrack.Contact.MobilePhone = suInfo.ContactMobilePhone;
                    sessionTrack.Contact.Email = suInfo.ContactEmail;
                    sessionTrack.Contact.AddressLine = suInfo.ContactAddressLine;
                    sessionTrack.Contact.AddressCity = suInfo.ContactAddressCity;
                    sessionTrack.Contact.AddressState = suInfo.ContactAddressState;
                    sessionTrack.Contact.AddressPostCode = suInfo.ContactAddressZipcode;
                    sessionTrack.Contact.AddressCountry = suInfo.ContactAddressCountry;
                    sessionTrack.Session.SessionID = suInfo.SessionId;
                    sessionTrack.Session.SessionGuid = suInfo.SessionGuid;
                    sessionTrack.SessionRegistration.NoteTitle = suInfo.NoteTitle;
                    sessionTrack.SessionRegistration.Note = suInfo.Note;
                    sessionTrack.SessionRegistration.OrderId = suInfo.OrderId;
                    sessionTrack.SessionRegistration.ChangeReason = suInfo.ChangeReason;
                    sessionTrack.SessionRegistration.AgentId = suInfo.AgentId;
                    sessionTrack.SessionRegistration.RegistrationStatus = suInfo.RegistrationStatus;
                    sessionTrack.RecordSource = RecordSource.Magento;
                    if (Enum.TryParse<MagentoTransactionType>(suInfo.TransactionType,true, out localMTransType))
                    {
                        sessionTrack.SessionRegistration.TransactionType = localMTransType;
                        sessionTrack.SessionRegistration.TransactionTypeString = suInfo.TransactionType;
                    }
                    sessionTrack.SessionRegistration.RegistrationId = suInfo.RegistrationId;
                    localTargetList.Add(sessionTrack);
                }
            }

            return localTargetList;
        }

        #region ProjectMilestone

        public static List<ProjectMilestoneResult> GetProjectMilestoneListFromProjectMilestoneUploadList(List<ProjectMilestoneUpload> _sourceList)
        {
            List<ProjectMilestoneResult> localTargetList = null;
            ProjectMilestoneResult projectMilestone;

            if (_sourceList != null && _sourceList.Count > 0)
            {
                localTargetList = new List<ProjectMilestoneResult>(_sourceList.Count);
                foreach (ProjectMilestoneUpload pmUpload in _sourceList)
                {
                    projectMilestone = new ProjectMilestoneResult();
                    projectMilestone.ReferenceNum = pmUpload.ReferenceNum;
                    projectMilestone.ProjectID = pmUpload.ProjectID;
                    projectMilestone.Description = pmUpload.Description;

                    localTargetList.Add(projectMilestone);
                }
            }

            return localTargetList;
        }

        #endregion

        /*public static ClientConfiguration GetFOClientConfig()
        {
            ClientConfiguration cc = new ClientConfiguration();

            cc.ActiveDirectoryTenant = ConfigurationManager.AppSettings["FO_ActiveDirectoryTenant"];
            cc.UriString = ConfigurationManager.AppSettings["FO_URL"];
            cc.ActiveDirectoryClientAppId = ConfigurationManager.AppSettings["FO_ApplicationID"];
            cc.ActiveDirectoryClientAppSecret = ConfigurationManager.AppSettings["FO_ApplicationSecret"];

            return cc;
        }*/

    }
}