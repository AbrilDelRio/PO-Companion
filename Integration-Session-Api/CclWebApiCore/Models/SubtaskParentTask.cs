using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CclWebApi.Models
{
    public class SubtaskParentTask
    {

        public SubtaskParentTask()
        {
            ParentTaskId = Guid.Empty;
            SessionRegistrationId = Guid.Empty;
        }
        public Guid ParentTaskId
        {
            get;
            set;
        }

        public int DelayExecutionFor
        {
            get;
            set;
        }

        public Guid SessionRegistrationId
        {
            get;
            set;
        }
        
    }
}