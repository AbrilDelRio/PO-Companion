using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CclWebApi.Helpers
{
    public class ValidationException : Exception
    {
        public ValidationException(string message) : base(message)
        {

        }
    }
}