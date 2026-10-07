using CclCrmProxyCore.Helpers;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Mvc;
using CclWebApi.Helpers;
using CclCrmProxyCore.Helpers;

namespace CclWebApi.Controllers
{
    //[Route("api/[controller]")]
    [ApiController]
    public class BaseCrmController : ControllerBase
    {
        protected IOrganizationService service;

        private IConfiguration _configuration;

        public BaseCrmController(IConfiguration configuration)
        {
            _configuration = configuration;
            service = CRMHelper.GetCrmService(Helper.GetCrmConnString(_configuration));
            
        }
        public BaseCrmController(string connectionString)
        {
            service = CRMHelper.GetCrmService(connectionString);
        }
    }
}
