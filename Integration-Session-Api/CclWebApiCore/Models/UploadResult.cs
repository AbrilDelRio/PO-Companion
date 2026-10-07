using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CclWebApi.Models
{
    public class UploadResult
    {
        public List<string> ProcessingErrors { get;set;} = new List<string>();
        public List<string> ProcessedOK = new List<string>();
        public List<SessionRegistrationResult> SessionRegistrationResults = new List<SessionRegistrationResult>();
        public string Summary
        {
            get { return $"Processed OK: {ProcessedOK.Count}, Errors: {ProcessingErrors.Count}"; }
        }
        public string ResultText
        {
            get;
            set;
        }

        public string Error { get; set; }

    }

    #region ProjectMilestone

    public class UploadProjectMilestoneResult : UploadResult
    {
      
        public List<ProjectMilestoneResult> ProjectMilestoneResults = new List<ProjectMilestoneResult>();

        public string ex { get; set; }

    }

    #endregion
}