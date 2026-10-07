using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CclWebApi.Models
{
    public class ProjectMilestoneResult
    {
        //public const string Success = "success";
        //public const string Failure = "failure";

        public ProjectMilestoneResult()
        {
            ReferenceNum = string.Empty;
            ProjectID = string.Empty;
            Description = string.Empty;
        }
        public string ReferenceNum
        {
            get;
            set;
        }

       public string ProjectID
       {
            get;
            set;
       }

       public string Description
       {
            get;
            set;
       }

       public string Message
       {
            get;
            set;
       }

       public static ProjectMilestoneResult CreateAndInitializeFrom(ProjectMilestone _prjMilestone)
       {
            ProjectMilestoneResult localResult = new ProjectMilestoneResult();
            if (_prjMilestone != null)
            {
                localResult.ReferenceNum = _prjMilestone.ReferenceNum;
                localResult.ProjectID = _prjMilestone.ProjectID;
                localResult.Description = _prjMilestone.Description;
 
            }

            return localResult;
       }
    }
}