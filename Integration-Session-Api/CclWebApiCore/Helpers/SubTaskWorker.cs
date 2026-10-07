using CclCrmProxyCore.Helpers;
using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Extensions.Logging;

namespace CclWebApi.Helpers
{
    public class SubTaskWorker
    {
        private readonly CclServiceContext cclContext;
        private readonly IOrganizationService service;
        private readonly ILogger logger;

        // Next WBS child number for the parent task this worker creates subtasks under. Seeded from
        // Dataverse on the first CreateSubtask call, then advanced in memory for the rest of the
        // batch. Re-reading per registration would add a Dataverse round trip inside the parent-task
        // lock and, worse, would depend on the row just created being visible to the next read - the
        // same commit-visibility lag that SubtaskCreator already works around elsewhere.
        private int? nextChildNumber;

        public SubTaskWorker(
            CclServiceContext _cclContext,
            IOrganizationService _service,
            ILogger _logger = null)
        {
            cclContext = _cclContext;
            service = _service;
            logger = _logger;
        }

        public msdyn_projecttask CreateSubtask(
            msevtmgt_SessionRegistration sr,
            msdyn_projecttask projTask)
        {
            msdyn_projecttask subTask = new msdyn_projecttask();
            subTask.msdyn_parenttask = projTask.ToEntityReference();
            subTask.msdyn_Effort = projTask.ccl_participantlevelofeffort;
            subTask.msdyn_project = projTask.msdyn_project;
            subTask.msdyn_WBSID =
                projTask.msdyn_WBSID + "." + TakeNextChildNumber(projTask).ToString();

            (DateTime? Start, DateTime? End) dates = ResolveSubtaskDates(projTask);

            subTask.msdyn_scheduledstart = dates.Start;
            subTask.msdyn_scheduledend = dates.End;

            // Keep the Task Form and Task Dashboard dates aligned. Because autoscheduling is
            // disabled for generated participant subtasks, both date pairs are populated here.
            subTask.msdyn_start = dates.Start;
            subTask.msdyn_finish = dates.End;

            // Duration is stored in msdyn_duration and uses inclusive calendar days:
            // same day = 1; 21st through 22nd = 2. The time portion is ignored.
            double? durationDays = CalculateInclusiveDurationDays(
                dates.Start,
                dates.End);

            subTask.msdyn_duration = durationDays;

            if (durationDays.HasValue)
            {
                logger?.LogInformation(
                    "Subtask duration calculated. ParentTaskId={ParentTaskId}, Start={Start}, End={End}, DurationDays={DurationDays}",
                    projTask.Id,
                    dates.Start,
                    dates.End,
                    durationDays.Value);
            }
            else
            {
                logger?.LogWarning(
                    "Subtask duration could not be calculated. ParentTaskId={ParentTaskId}, Start={Start}, End={End}",
                    projTask.Id,
                    dates.Start,
                    dates.End);
            }

            if (sr.FormattedValues.Contains("msevtmgt_contactid"))
            {
                subTask.msdyn_subject =
                    $"{projTask.msdyn_subject} - {sr.FormattedValues["msevtmgt_contactid"]}";
            }
            else
            {
                subTask.msdyn_subject = $"{projTask.msdyn_subject} - (no name)";
            }

            subTask.msdyn_transactioncategory = ResolveTransactionCategory(projTask);
            subTask.ccl_Participant = sr.msevtmgt_ContactId;
            subTask.ccl_RegistrationID = sr.ToEntityReference();
            subTask.msdyn_autoscheduling = false;

            MapParticipantDetails(subTask, sr, projTask);

            return subTask;
        }

        // ccl_subtaskcategory is the field authored specifically to categorize generated participant
        // subtasks; msdyn_transactioncategory is the parent task's own category and is used only when
        // the parent has no subtask-specific category set.
        private EntityReference ResolveTransactionCategory(msdyn_projecttask parentTask)
        {
            EntityReference category =
                parentTask.ccl_subtaskcategory ?? parentTask.msdyn_transactioncategory;

            string source = parentTask.ccl_subtaskcategory != null
                ? "ccl_subtaskcategory"
                : parentTask.msdyn_transactioncategory != null
                    ? "msdyn_transactioncategory"
                    : "none";

            logger?.LogInformation(
                "Subtask category resolved. ParentTaskId={ParentTaskId}, Source={Source}, CategoryId={CategoryId}",
                parentTask.Id,
                source,
                category?.Id);

            return category;
        }

        // Maps Hosting Platform and Participant Email to generated participant subtasks.
        private void MapParticipantDetails(
            msdyn_projecttask subTask,
            msevtmgt_SessionRegistration sr,
            msdyn_projecttask parentTask)
        {
            string hostingPlatform = ResolveHostingPlatform(
                sr,
                parentTask,
                out string hostingSource);

            subTask.new_HostingPlatform = hostingPlatform;

            logger?.LogInformation(
                "Subtask hosting platform resolved. ParentTaskId={ParentTaskId}, SessionRegistrationId={SessionRegistrationId}, SessionId={SessionId}, Source={Source}, Value={Value}",
                parentTask.Id,
                sr.Id,
                sr.msevtmgt_SessionId?.Id,
                hostingSource,
                hostingPlatform ?? "(none)");

            string participantEmail = ResolveParticipantEmail(sr, out string emailSource);

            if (!string.IsNullOrWhiteSpace(participantEmail))
            {
                subTask.new_ParticipantEmail = participantEmail;

                logger?.LogInformation(
                    "Subtask participant email resolved. ParentTaskId={ParentTaskId}, SessionRegistrationId={SessionRegistrationId}, Source={Source}, Found=true",
                    parentTask.Id,
                    sr.Id,
                    emailSource);
            }
            else
            {
                // A missing participant email does not block subtask creation.
                logger?.LogWarning(
                    "Subtask participant email not found. ParentTaskId={ParentTaskId}, SessionRegistrationId={SessionRegistrationId}, ContactId={ContactId}",
                    parentTask.Id,
                    sr.Id,
                    sr.msevtmgt_ContactId?.Id);
            }
        }

        // Resolves Hosting Platform in this order:
        // 1) Session Registration text field
        // 2) Parent Project Task text field
        // 3) Related Session ccl_hostingplatform Choice label
        private string ResolveHostingPlatform(
            msevtmgt_SessionRegistration sessionRegistration,
            msdyn_projecttask parentTask,
            out string source)
        {
            source = "none";

            if (!string.IsNullOrWhiteSpace(sessionRegistration.new_HostingPlatform))
            {
                source = "SessionRegistration.new_hostingplatform";
                return sessionRegistration.new_HostingPlatform;
            }

            if (!string.IsNullOrWhiteSpace(parentTask.new_HostingPlatform))
            {
                source = "ParentTask.new_hostingplatform";
                return parentTask.new_HostingPlatform;
            }

            if (sessionRegistration.msevtmgt_SessionId == null)
            {
                logger?.LogWarning(
                    "Cannot resolve Hosting Platform from Session because Session Registration has no Session lookup. SessionRegistrationId={SessionRegistrationId}",
                    sessionRegistration.Id);

                return null;
            }

            try
            {
                Entity session = service.Retrieve(
                    "msevtmgt_session",
                    sessionRegistration.msevtmgt_SessionId.Id,
                    new ColumnSet("ccl_hostingplatform"));

                if (session.FormattedValues.TryGetValue(
                        "ccl_hostingplatform",
                        out string hostingPlatformLabel)
                    && !string.IsNullOrWhiteSpace(hostingPlatformLabel))
                {
                    source = "Session.ccl_hostingplatform";
                    return hostingPlatformLabel;
                }

                OptionSetValue hostingPlatformValue =
                    session.GetAttributeValue<OptionSetValue>("ccl_hostingplatform");

                if (hostingPlatformValue != null)
                {
                    logger?.LogWarning(
                        "Session Hosting Platform has value {HostingPlatformValue}, but its formatted label was not returned. SessionId={SessionId}",
                        hostingPlatformValue.Value,
                        session.Id);
                }
            }
            catch (Exception ex)
            {
                // A missing or unavailable Hosting Platform does not block subtask creation.
                logger?.LogWarning(
                    ex,
                    "Failed to retrieve Hosting Platform from related Session. SessionRegistrationId={SessionRegistrationId}, SessionId={SessionId}",
                    sessionRegistration.Id,
                    sessionRegistration.msevtmgt_SessionId.Id);
            }

            return null;
        }

        // Session Registration has no email field; use the related Contact's primary email.
        private string ResolveParticipantEmail(
            msevtmgt_SessionRegistration sr,
            out string source)
        {
            source = "none";

            if (sr.msevtmgt_ContactId == null)
            {
                return null;
            }

            Contact contact = cclContext.ContactSet
                .Where(c => c.Id == sr.msevtmgt_ContactId.Id)
                .FirstOrDefault();

            if (contact == null || string.IsNullOrWhiteSpace(contact.EMailAddress1))
            {
                return null;
            }

            source = "Contact.emailaddress1";
            return contact.EMailAddress1;
        }

        // Prefers header dates from the parent task and falls back to its scheduled dates.
        private (DateTime? Start, DateTime? End) ResolveSubtaskDates(
            msdyn_projecttask parentTask)
        {
            DateTime? start =
                parentTask.ccl_headerstartdate ?? parentTask.msdyn_scheduledstart;

            DateTime? end =
                parentTask.ccl_headerenddate ?? parentTask.msdyn_scheduledend;

            string startSource = parentTask.ccl_headerstartdate.HasValue
                ? "ccl_headerstartdate"
                : "msdyn_scheduledstart";

            string endSource = parentTask.ccl_headerenddate.HasValue
                ? "ccl_headerenddate"
                : "msdyn_scheduledend";

            logger?.LogInformation(
                "Subtask dates resolved. ParentTaskId={ParentTaskId}, StartSource={StartSource}, Start={Start}, EndSource={EndSource}, End={End}",
                parentTask.Id,
                startSource,
                start,
                endSource,
                end);

            return (start, end);
        }

        // Calculates inclusive calendar days and ignores the time portion.
        // Valid examples:
        // 2026-07-21 to 2026-07-21 = 1
        // 2026-07-21 to 2026-07-22 = 2
        // Missing dates or an end date before the start date return null.
        internal static double? CalculateInclusiveDurationDays(
            DateTime? start,
            DateTime? end)
        {
            if (!start.HasValue || !end.HasValue)
            {
                return null;
            }

            DateTime startDate = start.Value.Date;
            DateTime endDate = end.Value.Date;

            if (endDate < startDate)
            {
                return null;
            }

            return (endDate - startDate).Days + 1d;
        }

        // Seeds the counter from Dataverse once per worker instance, then hands out numbers locally.
        // A worker instance is scoped to a single parent task within a single request (see
        // SubtaskCreator.CreateSubtasks), so the counter can never leak across parent tasks.
        //
        // A create that fails after taking a number leaves a gap in the sequence. That is deliberate
        // and harmless - WBS child numbers only need to be unique and ordered - and is far preferable
        // to handing the same number to two subtasks.
        private int TakeNextChildNumber(msdyn_projecttask projTask)
        {
            (int childNumber, int next) = SubtaskLockPolicy.TakeChildNumber(
                nextChildNumber,
                () => GetNextChildNumber(projTask));

            nextChildNumber = next;

            return childNumber;
        }

        private int GetNextChildNumber(msdyn_projecttask projTask)
        {
            int maxChildNo = 1;

            var fetchXml = $@"<fetch>
                              <entity name='msdyn_projecttask'>
                                <attribute name='msdyn_wbsid'/>
                                <attribute name='msdyn_parenttask'/>
                                <attribute name='msdyn_project'/>
                                <filter type='and'>
                                  <condition attribute='msdyn_parenttask' operator='eq' value='{projTask.Id}'/>
                                  <filter type='and'>
                                    <condition attribute='msdyn_project' operator='eq' value='{projTask.msdyn_project.Id}'/>
                                  </filter>
                                </filter>
                                <order attribute='createdon' descending='true'/>
                              </entity>
                            </fetch>";

            EntityCollection localChildList =
                service.RetrieveMultiple(new FetchExpression(fetchXml));

            foreach (Entity localEntity in localChildList.Entities)
            {
                int current = 0;

                if (localEntity.Contains("msdyn_wbsid"))
                {
                    string wbsId = localEntity.Attributes["msdyn_wbsid"].ToString();

                    if (wbsId.Contains("."))
                    {
                        int.TryParse(wbsId.Split('.').Last(), out current);

                        if (current >= maxChildNo)
                        {
                            maxChildNo = ++current;
                        }
                    }
                    else
                    {
                        maxChildNo++;
                    }
                }
            }

            return maxChildNo;
        }
    }
}