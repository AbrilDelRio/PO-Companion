using CclWebApi.Models;
using CclWebApi.Services.CopyProjectTemplate;
using Microsoft.AspNetCore.Mvc;

namespace CclWebApi.Controllers
{
    [Route("")]
    public class CopyProjectTemplateController : BaseCrmController
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<CopyProjectTemplateController> _logger;

        public CopyProjectTemplateController(IConfiguration configuration, ILogger<CopyProjectTemplateController> logger)
            : base(configuration)
        {
            _configuration = configuration;
            _logger = logger;
        }

        [HttpPost("CopyProjectTemplate")]
        public IActionResult CopyProjectTemplate([FromBody] CopyProjectTemplateRequest request)
        {
            string transactionId = Guid.NewGuid().ToString().ToUpperInvariant().Substring(0, 8);

            if (request == null)
            {
                return BadRequest(new CopyProjectTemplateResponse
                {
                    Success = false,
                    Message = "Request body is required.",
                    TransactionId = transactionId
                });
            }

            if (request.TargetProjectId == Guid.Empty)
            {
                return BadRequest(new CopyProjectTemplateResponse
                {
                    Success = false,
                    Message = "TargetProjectId is required.",
                    TransactionId = transactionId
                });
            }

            if (service == null)
            {
                // CRMHelper.GetCrmService already logged the underlying cause.
                _logger.LogError("[{TransactionId}] CopyProjectTemplate: CRM service unavailable for project {TargetProjectId}.",
                    transactionId, request.TargetProjectId);

                return StatusCode(StatusCodes.Status503ServiceUnavailable, new CopyProjectTemplateResponse
                {
                    Success = false,
                    Message = "CRM service is unavailable.",
                    TransactionId = transactionId,
                    TargetProjectId = request.TargetProjectId,
                    TargetLogicalName = request.TargetLogicalName
                });
            }

            // The copy writes its step-by-step trace to mfd_pocopyprojectlog in Dataverse. That trace
            // is unreachable when Dataverse itself is the thing failing, so the start/finish/failure
            // of every run is also emitted here — this is the only record that reaches Rapid7 and
            // App Insights, and the transaction id ties the two together.
            _logger.LogInformation("[{TransactionId}] CopyProjectTemplate started for project {TargetProjectId} ({TargetLogicalName}).",
                transactionId, request.TargetProjectId, request.TargetLogicalName);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                CopyProjectTemplateApiService copyService = new CopyProjectTemplateApiService(service,_configuration.GetConnectionString("CRMConnection"));

                CopyProjectTemplateExecutionResult result = copyService.Execute(request, transactionId);

                _logger.LogInformation("[{TransactionId}] CopyProjectTemplate completed for project {TargetProjectId} in {ElapsedMs} ms (configuration {ConfigurationCode}).",
                    transactionId, result.TargetProjectId, stopwatch.ElapsedMilliseconds, result.ConfigurationCode);

                return Ok(new CopyProjectTemplateResponse
                {
                    Success = true,
                    Message = "Project template copied successfully.",
                    TransactionId = transactionId,
                    TargetProjectId = result.TargetProjectId,
                    TargetLogicalName = result.TargetLogicalName,
                    ConfigurationCode = result.ConfigurationCode,
                    OrganizationName = result.OrganizationName
                });
            }
            catch (Exception ex)
            {
                // A failed copy leaves the target project partially populated — there is no
                // transaction spanning the ExecuteMultiple batches — so this is logged at Error and
                // needs manual reconciliation against mfd_pocopyprojectlog.
                _logger.LogError(ex, "[{TransactionId}] CopyProjectTemplate FAILED for project {TargetProjectId} after {ElapsedMs} ms. The project may be partially copied.",
                    transactionId, request.TargetProjectId, stopwatch.ElapsedMilliseconds);

                return StatusCode(StatusCodes.Status500InternalServerError, new CopyProjectTemplateResponse
                {
                    Success = false,
                    Message = ex.Message,
                    TransactionId = transactionId,
                    TargetProjectId = request.TargetProjectId,
                    TargetLogicalName = string.IsNullOrWhiteSpace(request.TargetLogicalName)
                        ? "msdyn_project"
                        : request.TargetLogicalName
                });
            }
        }
    }
}
