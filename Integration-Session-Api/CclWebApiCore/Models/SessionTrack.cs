using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CclWebApi.Models
{
    public enum RecordSource
    {
        Excel = 1,
        Magento
    }
    public enum MagentoTransactionType
    {
        New = 1,
        Transfer = 2,
        Pending = 3,
        Cancel = 4
    }
    public class SessionTrack
    {
        /// <summary>
        /// Record number from source Excel, starting from 1
        /// </summary>
        public int RecordNo { get; set; }
        public Contact Contact { get; set; }
        public Session Session { get; set; }
        public SessionRegistration SessionRegistration { get; set; }
        public string SessionTrackID { get; set; }

        public string SessionTrackGuid { get; set; }
        public string Title { get; set; }
        public string Note { get; set; }
        public RecordSource RecordSource { get; set; }

        public SessionTrack()
        {
            this.Contact = new Contact();
            this.Session = new Session();
            this.SessionRegistration = new SessionRegistration();
        }

        public bool hasSessionTrackId()
        {
            return !string.IsNullOrWhiteSpace(this.SessionTrackID) || !string.IsNullOrWhiteSpace(this.SessionTrackGuid);
        }
    }

    public class Contact
    {
        public string Salutation { get; set; }
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }
        public string Suffix { get; set; }
        public string JobTitle { get; set; }
        public string CompanyName { get; set; }
        public string Telephone { get; set; }
        public string MobilePhone { get; set; }
        public string Email { get; set; }
        public string AddressLine { get; set; }
        public string AddressCity { get; set; }
        public string AddressState { get; set; }
        public string AddressCountry { get; set; }
        public string AddressPostCode { get; set; }
    }

    public class Session
    {
        public string SessionID { get; set; }
        public string Title { get; set; }
        public string Note { get; set; }

        public string SessionGuid { get; set; }

        public bool hasSessionId()
        {
            return !string.IsNullOrWhiteSpace(this.SessionID) || !string.IsNullOrWhiteSpace(this.SessionGuid);
        }
    }
    [System.Runtime.Serialization.DataContract]
    public class SessionRegistration
    {
        public string NoteTitle { get; set; }

        public string Note { get; set; }

        [System.Runtime.Serialization.DataMember]
        public string ChangeReason { get; set; }

        [System.Runtime.Serialization.DataMember]
        public string OrderId { get; set; }

        public MagentoTransactionType TransactionType { get; set; }

        [System.Runtime.Serialization.DataMember]
        public string TransactionTypeString { get; set; }

        public string RegistrationId { get; set; }

        [System.Runtime.Serialization.DataMember]
        public string AgentId { get; set; }

        [System.Runtime.Serialization.DataMember]
        public string RegistrationStatus { get; set; }


        /*public string Status { get; set; }
        public string ScholarshipRecipient { get; set; }*/
    }
}