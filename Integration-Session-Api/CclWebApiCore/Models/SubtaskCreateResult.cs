using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Text.Json.Serialization;

namespace CclWebApi.Models
{
    // Why CreateSubtasks stopped, in a form the controller can branch on without string-matching
    // Message. Completed covers every outcome the request itself resolved - a create, a duplicate
    // skip, a missing parent task - and is deliberately the default so any future path that forgets
    // to set it keeps today's HTTP 200 behaviour.
    public enum SubtaskOutcome
    {
        Completed = 0,
        LockTimeout = 1
    }

    public class SubtaskCreateResult
    {
        public string Message
        {
            get;
            set;
        }

        // Kept out of the serialized body on purpose: the Dataverse plug-in parses the response as
        // {"message":"..."} and adding a field would change that contract. Both JsonIgnore
        // attributes are applied so the exclusion holds whether MVC serializes with
        // System.Text.Json or Newtonsoft.
        [JsonIgnore]
        [Newtonsoft.Json.JsonIgnore]
        public SubtaskOutcome Outcome
        {
            get;
            set;
        }
    }
}
