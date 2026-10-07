using CclWebApi.Helpers;
using CclWebApi.Models;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk;
using CclCrmProxyCore.Helpers;

namespace CclWebApi.Controllers
{
    [Route("")]
    public class SessionController : BaseCrmController
    {

        IConfiguration _configuration;
        private readonly ILogger<SessionController> _logger;

        public SessionController(IConfiguration configuration, ILogger<SessionController> logger) : base(configuration)
        {
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("GetContactInfo/{contactId}")]
        public async Task<IActionResult> GetContact(string contactId)
        {
            HttpRequest request = this.Request;
            UploadResult uplResult = new UploadResult();
            string transId = Guid.NewGuid().ToString().ToUpperInvariant().Substring(0, 8);
            var crmConnString = _configuration.GetConnectionString("CRMConnection");
            var svc = CRMHelper.GetCrmService(crmConnString);
            if (svc != null)
            {
                var contact = svc.Retrieve("contact", new Guid(contactId),new ColumnSet("firstname", "lastname"));
                if (contact != null)
                {
                    var result = new
                    {
                        FirstName = contact.GetAttributeValue<string>("firstname"),
                        LastName = contact.GetAttributeValue<string>("lastname"),
                    };
                    _logger.LogInformation($"Success: GetContact. TransactionId={transId}, ContactId={contactId}");
                    return (Ok(result));
                }
            }
            _logger.LogWarning($"Skipped: ContactNotFound. TransactionId={transId}, ContactId={contactId}");
            return NotFound(new { Message = "Contact not found" });

        }

        [HttpPost("CreateSubtasks")]
        public async Task<IActionResult> CreateSubtasks()
        {
            HttpRequest request = this.Request;
            SubtaskCreateResult result = new SubtaskCreateResult();
            SubtaskParentTask parentTask = null;
            string localJson;
            string transId = Guid.NewGuid().ToString().ToUpperInvariant().Substring(0, 8);
            try
            {
                using (StreamReader reader = new StreamReader(request.Body))
                {
                    localJson = await reader.ReadToEndAsync();
                }
                parentTask = Helper.GetObjectFromJson<SubtaskParentTask>(localJson);
                if (parentTask != null && parentTask.ParentTaskId != Guid.Empty)
                {
                    _logger.LogInformation($"ParentTaskId: {parentTask.ParentTaskId}");
                    result = new SubtaskCreator(_configuration, _logger).CreateSubtasks(parentTask);
                }
                else
                {
                    result.Message = $"Skipped: InvalidRequestPayload. TransactionId={transId}, ParentTaskId={parentTask?.ParentTaskId.ToString() ?? "null"}, SessionRegistrationId={parentTask?.SessionRegistrationId.ToString() ?? "null"}";
                    _logger.LogWarning(result.Message);
                }

                if (result.Outcome == SubtaskOutcome.LockTimeout)
                {
                    // 409 rather than 200: the caller has to be able to tell "another request holds
                    // this parent task's lock, the work was not done" from "there was nothing to do".
                    // Returned as 4xx on purpose - nginx and APIM retry 502/503/504 on their own, and
                    // an automatic retry can land while the abandoned request is still running and
                    // still holding the lock. The response body is unchanged, so a caller that parses
                    // Message keeps working exactly as before.
                    return Conflict(result);
                }
            }
            catch (Exception ex)
            {
                string idContext = parentTask != null
                    ? $"TransactionId={transId}, ParentTaskId={parentTask.ParentTaskId}, SessionRegistrationId={parentTask.SessionRegistrationId}"
                    : $"TransactionId={transId}, ParentTaskId=unknown, SessionRegistrationId=unknown";
                _logger.LogError(ex, $"{SessionRegistrationResult.Failure}: {ex.Message}. {idContext}");
                result.Message = $"{SessionRegistrationResult.Failure}: {ex.Message}. {idContext}";

                // Left as 200 deliberately. PartialSubtaskCreationFailure arrives here, and promoting
                // it to 5xx would make the proxies in front of this API retry it automatically - the
                // retry-onto-a-held-lock hazard above. Changing this one needs the plug-in's retry
                // behaviour agreed first; the failure is still visible in Message today.
            }
            return Ok(result);
        }

        [HttpPost("UploadSessionInfo")]
        public async Task<IActionResult> UploadSessionInfo()
        {
            string localJson;
            List<SessionTrack> localSessionTrackList;
            UploadResult uploadResult = new UploadResult();

            HttpRequest localRequest = this.Request;

            string transId = Guid.NewGuid().ToString().ToUpperInvariant().Substring(0, 8);
            DateTime start = DateTime.Now;
            try
            {
                using (StreamReader reader = new StreamReader(localRequest.Body))
                {
                    localJson = await reader.ReadToEndAsync();
                }

                var uploadInfos = Helper.GetListFromJson<SessionUploadInfo>(localJson);
                foreach (var info in uploadInfos)
                {
                    _logger.LogInformation($"UploadSessionInfo item. TransactionId={transId}, SessionGuid={info.SessionGuid}, OrderId={info.OrderId}, RegistrationStatus={info.RegistrationStatus}, ChangeReason={info.ChangeReason}, TransactionType={info.TransactionType}");
                }

                localSessionTrackList = Helper.GetSessionTrackListFromSessionUploadInfoList(uploadInfos);
                if (localSessionTrackList != null && localSessionTrackList.Count > 0)
                {
                    uploadResult = new CrmWorker(_configuration).ProcessSessionTracks(localSessionTrackList);
                    TimeSpan duration = DateTime.Now.Subtract(start);
                    _logger.LogInformation($"Success: UploadSessionInfo. TransactionId={transId}, Tracks={localSessionTrackList.Count}, {uploadResult.Summary}. Execution time: {duration.ToString(@"hh\:mm\:ss")}");
                }
                else
                {
                    // Previously silent: an unparseable or empty payload returned an empty result with no log at all.
                    _logger.LogWarning($"Skipped: NoSessionTracksInPayload. TransactionId={transId}, PayloadLength={localJson?.Length ?? 0}");
                }
            }
            catch (Exception _error)
            {
                _logger.LogError(_error, $"Failure: UploadSessionInfo. TransactionId={transId}: {_error.Message}");
                uploadResult.Error = _error.Message;
            }

            // Per-item validation failures are returned to the caller but were never logged, so a
            // partially-failed upload was indistinguishable from a clean one in InsightOps.
            foreach (string error in uploadResult.ProcessingErrors)
                _logger.LogError($"UploadSessionInfo ProcessingError. TransactionId={transId}: {error}");

            return Ok(JToken.FromObject(uploadResult));

        }

        [HttpPost("CreateProjectMilestone")]
        public async Task<IActionResult> CreateProjectMilestone()
        {
            HttpRequest request = this.Request;
            List<ProjectMilestoneResult> projectMilestoneList;
            string localJson;

            UploadProjectMilestoneResult resultMilestone = new UploadProjectMilestoneResult();

            string transId = Guid.NewGuid().ToString().ToUpperInvariant().Substring(0, 8);
            DateTime start = DateTime.Now;
            try
            {
                using (StreamReader reader = new StreamReader(request.Body))
                {
                    localJson = await reader.ReadToEndAsync();
                }
                projectMilestoneList = Helper.GetProjectMilestoneListFromProjectMilestoneUploadList(Helper.GetListFromJson<ProjectMilestoneUpload>(localJson));
                if (projectMilestoneList != null && projectMilestoneList.Count > 0)
                {
                    resultMilestone = new CrmWorker(_configuration).ProcessProjectMilestones(projectMilestoneList);
                    TimeSpan duration = DateTime.Now.Subtract(start);
                    _logger.LogInformation($"Success: CreateProjectMilestone. TransactionId={transId}, Milestones={projectMilestoneList.Count}, {resultMilestone.Summary}. Execution time: {duration.ToString(@"hh\:mm\:ss")}");
                }
                else
                {
                    // Previously silent: an unparseable or empty payload returned an empty result with no log at all.
                    _logger.LogWarning($"Skipped: NoMilestonesInPayload. TransactionId={transId}, PayloadLength={localJson?.Length ?? 0}");
                }
            }
            catch (Exception _ex)
            {
                _logger.LogError(_ex, $"{SessionRegistrationResult.Failure}: CreateProjectMilestone. TransactionId={transId}: {_ex.Message}");
                resultMilestone.ex = $"{SessionRegistrationResult.Failure}: {_ex.Message}";
            }

            foreach (string error in resultMilestone.ProcessingErrors)
                _logger.LogError($"CreateProjectMilestone ProcessingError. TransactionId={transId}: {error}");

            return Ok(resultMilestone);
        }
    }
}
