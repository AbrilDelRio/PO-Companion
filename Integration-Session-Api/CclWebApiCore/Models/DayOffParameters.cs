using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CclWebApi.Models
{
    public class DayOffParameters
    {
        public string ResourceId
        {
            get;
            set;
        }

        public string Description
        {
            get;
            set;
        }

        public string DateOff
        {
            get;
            set;
        }
    }
}