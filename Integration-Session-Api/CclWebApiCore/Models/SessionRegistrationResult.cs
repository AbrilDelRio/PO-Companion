using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CclWebApi.Models
{
    public class SessionRegistrationResult
    {
        public const string Success = "success";
        public const string Failure = "failure";
        public string RegistrationId
        {
            get;
            set;
            
        }

        public string OrderId
        {
            get;
            set;
        }

        public string SessionId
        {
            get;
            set;
        }

        public string SessionGuid
        {
            get;
            set;
        }

        public string SessionTrackId
        {
            get;
            set;
        }

        public string Message
        {
            get;
            set;
        }

        public static SessionRegistrationResult CreateAndInitializeFrom(SessionTrack _sessionTrack)
        {
            SessionRegistrationResult localResult = new SessionRegistrationResult();
            if(_sessionTrack !=null)
            {
                localResult.OrderId = _sessionTrack.SessionRegistration.OrderId;
                localResult.SessionId = _sessionTrack.Session.SessionID;
                localResult.SessionTrackId = _sessionTrack.SessionTrackID;
                localResult.RegistrationId = _sessionTrack.SessionRegistration.RegistrationId;
                localResult.SessionGuid = _sessionTrack.Session.SessionGuid;
            }

            return localResult;
        }
    }
}