using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Web;
using CclWebApi.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using CclCrmProxyCore.Helpers;
using Microsoft.Xrm.Sdk.Messages;


namespace CclWebApi.Helpers
{
    public class SubtaskCreator
    {
        IOrganizationService service;

        IConfiguration _configuration;
        ILogger _logger;

        // DelayExecutionFor exists to wait out Dataverse transaction-commit latency for a caller that
        // queries immediately after its own write. It must never again be allowed to hold
        // hso_projecttasklock for its full duration (see CreateSubtasks), so cap it as defense in depth.
        private const int MaxDelayExecutionForMs = 30000;

        // A lock is only ever meant to live for the few seconds it takes to query and create subtasks.
        // If a process dies mid-request (crash, redeploy, OOM-kill) its finally block never runs and the
        // lock is orphaned. cclsandboxqa had one from 2026-05-11 that silently blocked a project task's
        // subtask creation for two months. Anything older than this is treated as abandoned.
        private static readonly TimeSpan StaleLockThreshold = TimeSpan.FromMinutes(10);

        // Concurrent CreateSubtasks requests for different Session Registrations under the same Parent
        // Task are expected (each registration's confirmation fires its own request). A request that
        // finds the parent task locked waits for the holder to finish instead of dropping the work: QA
        // saw two concurrent registrations under one parent task where the second returned
        // "Skipped: ParentTaskLocked" and never created a subtask. Observed request duration is
        // normally ~3-10s; each request only ever holds the lock long enough to create one subtask, so
        // N requests queued on the same parent task need roughly (N-1) x that long before the last one
        // can proceed. 20s covers a couple of queued waiters comfortably but not an arbitrary pile-up, so
        // it's overridable via SubtaskCreator:LockWaitTimeoutSeconds (see GetLockWaitTimeout) without a
        // code change - still bounded, never unbounded.
        private static readonly TimeSpan DefaultLockWaitTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan LockPollInterval = TimeSpan.FromSeconds(2);
        private const string LockWaitTimeoutConfigKey = "SubtaskCreator:LockWaitTimeoutSeconds";

        // Operator kill switch for the reconcile-the-whole-parent-task behaviour described in
        // CreateSubtasks. Defaults to on; see SubtaskLockPolicy.ResolveReconcileAllRegistrations.
        private const string ReconcileAllRegistrationsConfigKey = "SubtaskCreator:ReconcileAllRegistrations";

        // Dataverse's standard "record does not exist" fault (0x80040217), returned when a Delete
        // targets a record that's already gone. Used to recognize a concurrent stale-lock reclaim below.
        private const int ObjectDoesNotExistErrorCode = -2147220969;

        // Dataverse's duplicate-key fault (0x80040237). The lock row uses ParentTaskId as its
        // deterministic primary key, so this fault means another request acquired or is releasing the
        // same lock. Dataverse visibility can briefly lag behind the uniqueness check; therefore an
        // immediate read may return no row even though the duplicate-key fault is genuine contention.
        private const int DuplicateKeyErrorCode = -2147220937;

        public SubtaskCreator(IConfiguration configuration, ILogger logger)
        {
            _configuration = configuration;
            _logger = logger;
            service = CRMHelper.GetCrmService(Helper.GetCrmConnString(_configuration));

        }

        public SubtaskCreateResult CreateSubtasks(SubtaskParentTask _parentTask)
        {
            SubtaskCreateResult result = new SubtaskCreateResult();
            msdyn_project project;
            msdyn_projecttask parentTask;
            List<msevtmgt_SessionRegistration> registrations;
            DateTime start;
            int createdCount = 0;

            string idContext = $"ParentTaskId={_parentTask.ParentTaskId}, SessionRegistrationId={_parentTask.SessionRegistrationId}";

            using (CclServiceContext localContext = new CclServiceContext(service))
            {
                start = DateTime.Now;

                parentTask = localContext.msdyn_projecttaskSet.Where(x => x.msdyn_projecttaskId == _parentTask.ParentTaskId).FirstOrDefault();

                if (parentTask == null)
                {
                    result.Message = $"Skipped: ParentTaskNotFound. {idContext}";
                    _logger.LogWarning(result.Message);
                }
                else if (!parentTask.ccl_CreateParticipantSubtasks.HasValue || !parentTask.ccl_CreateParticipantSubtasks.Value)
                {
                    result.Message = $"Skipped: CreateParticipantSubtasksDisabled. {idContext}";
                    _logger.LogInformation(result.Message);
                }
                else if (!parentTask.ccl_participantlevelofeffort.HasValue || parentTask.ccl_participantlevelofeffort <= 0)
                {
                    result.Message = $"Skipped: ParticipantLevelOfEffortNotSet. {idContext}";
                    _logger.LogInformation(result.Message);
                }
                else
                {
                    project = localContext.msdyn_projectSet.Where(x => x.msdyn_projectId == parentTask.msdyn_project.Id).FirstOrDefault();

                    if (project == null)
                    {
                        result.Message = $"Skipped: ProjectNotFound. {idContext}";
                        _logger.LogWarning(result.Message);
                    }
                    else if (project.ccl_RelatedSession == null)
                    {
                        result.Message = $"Skipped: ProjectNotLinkedToSession. {idContext}";
                        _logger.LogInformation(result.Message);
                    }
                    else
                    {
                        // API-side deduplication and reconciliation. Note this is not request coalescing:
                        // every registration-triggered request still reaches this API and is still
                        // answered individually. What changes is that only one of them performs the
                        // parent-level work and the rest recognise it was already done.
                        //
                        // Every confirmed registration fires its own asynchronous plug-in job, so one
                        // upload produces N concurrent CreateSubtasks requests against the same Parent
                        // Task (UAT measured 16 registrations x 2 eligible parents). Serialising those on
                        // the lock and giving each a 20s budget lost between 1 and 7 of every 16 subtasks.
                        // Two changes remove the duplicated work without the plug-in changing at all:
                        //
                        //   1. Whichever request wins the lock reconciles EVERY confirmed registration
                        //      under this Parent Task, not just the one it was called for, so the batch is
                        //      done once instead of N times.
                        //   2. A request that can see the reconciliation is already durably finished
                        //      returns straight away - before taking the lock, and again while waiting for
                        //      it - instead of queueing for a lock it does not need.
                        //
                        // Both are idempotent and neither changes the set of records that ends up in
                        // Dataverse.
                        bool reconcileAll = GetReconcileAllRegistrations();
                        Guid targetRegistrationId = _parentTask.SessionRegistrationId;

                        // Only meaningful when a specific registration was asked for. A Guid.Empty sweep
                        // has no single "already done" condition - another registration can be confirmed at
                        // any moment - so it never short-circuits.
                        Func<bool> reconciliationComplete = targetRegistrationId == Guid.Empty
                            ? () => false
                            : () => ParentReconciliationComplete(parentTask.Id, targetRegistrationId);

                        // The lock this execution created, if any. Only ever set to a lock we just inserted
                        // ourselves (see AcquireParentTaskLock) - never another request's row - so the finally
                        // block below can never delete a lock it doesn't own.
                        hso_projecttasklock ownedLock = null;

                        try
                        {
                            if (reconciliationComplete())
                            {
                                // Answered without touching the lock. Once the winning request has
                                // reconciled the batch this becomes the common path, and it is what keeps
                                // the losing requests cheap instead of contending.
                                result.Message = $"Skipped: SubtasksAlreadyExist. {idContext}";
                                _logger.LogInformation(result.Message);

                                return result;
                            }

                            (hso_projecttasklock AcquiredLock, LockWaitOutcome Outcome) acquisition =
                                AcquireParentTaskLock(parentTask, _parentTask.DelayExecutionFor, idContext, reconciliationComplete);

                            ownedLock = acquisition.AcquiredLock;

                            if (acquisition.Outcome == LockWaitOutcome.TargetRegistrationAlreadyCreated)
                            {
                                // Another request reconciled this registration while this one waited. The
                                // work is done, so this is a success for the caller, not a lock failure.
                                result.Message = $"Skipped: SubtasksAlreadyExist. {idContext}";
                                _logger.LogInformation(result.Message);
                            }
                            else if (acquisition.Outcome == LockWaitOutcome.TimedOut)
                            {
                                result.Message = $"Failure: ParentTaskLockTimeout. {idContext}";
                                result.Outcome = SubtaskOutcome.LockTimeout;
                                _logger.LogError(result.Message);
                            }
                            else
                            {
                                if (reconcileAll || targetRegistrationId == Guid.Empty)
                                {
                                    registrations = localContext.msevtmgt_SessionRegistrationSet.Where(x => x.msevtmgt_SessionId.Id == project.ccl_RelatedSession.Id
                                                                                                        && x.ccl_registrationstatus == new OptionSetValue(803280001)).ToList();
                                }
                                else
                                {
                                    registrations = localContext.msevtmgt_SessionRegistrationSet.Where(x => x.msevtmgt_SessionId.Id == project.ccl_RelatedSession.Id
                                                                                                        && x.ccl_registrationstatus == new OptionSetValue(803280001)
                                                                                                        && x.msevtmgt_SessionRegistrationId == _parentTask.SessionRegistrationId).ToList();
                                }

                                // Whether the registration this request was actually called for was part of
                                // the batch. Under reconcileAll the batch is every confirmed registration,
                                // so a request naming a registration that is not confirmed would otherwise
                                // report the other registrations' successes as its own.
                                bool targetWasProcessed = targetRegistrationId == Guid.Empty
                                    || registrations.Any(r => r.Id == targetRegistrationId);

                                // Fresh read, taken once under the lock: every subtask that already exists for
                                // this parent task, keyed by the registration it was created for. Checked
                                // per-registration below (req: idempotency must work whether SessionRegistrationId
                                // is empty and the loop processes every confirmed registration, or a single one).
                                HashSet<Guid> existingSubtaskRegistrationIds = RetrieveExistingSubtaskRegistrationIds(parentTask.Id);

                                // Idempotency check happens per-registration, under the lock: a retried request
                                // (or a second request that waited for this same lock) must not create a second
                                // subtask for a registration that already has one. Each registration's create is
                                // isolated (see ProcessRegistrations): one registration's failure - transient
                                // Dataverse error, throttling, anything - must not prevent the remaining
                                // registrations in this batch (e.g. the SessionRegistrationId=Guid.Empty sweep of
                                // every confirmed registration under one Parent Task) from being attempted.
                                //
                                // One worker for the whole batch so the WBS child-number counter is seeded
                                // from Dataverse once and then advanced in memory (see
                                // SubTaskWorker.CreateSubtask). A worker per registration would re-query for
                                // the next number after every insert, which both adds a round trip inside the
                                // parent-task lock and makes the number depend on the just-created row already
                                // being visible to that read.
                                SubTaskWorker subTaskWorker = new SubTaskWorker(localContext, service, _logger);

                                (createdCount, List<Exception> failures) = SubtaskLockPolicy.ProcessRegistrations(
                                    registrations,
                                    r => r.Id,
                                    existingSubtaskRegistrationIds,
                                    r => DupSubTaskRequest(subTaskWorker.CreateSubtask(r, parentTask)),
                                    r => _logger.LogInformation($"Skipped: SubtaskAlreadyExists. {idContext}, SessionRegistrationId={r.Id}"));

                                // Bring the parent rollup in line with the complete current set of child
                                // tasks. Derived from Dataverse rather than incremented, so it stays
                                // idempotent across retries, and it runs inside the parent-task lock so
                                // concurrent registrations cannot overwrite one another with a partial total.
                                //
                                // Called unconditionally, not gated on createdCount. ReconcileParentEffort
                                // itself decides whether an Update is actually needed, so the expensive part -
                                // the Update, which fires CclCrmPlugin.ProjectTaskPostUpdateFDD025
                                // synchronously - still only happens when the value really changed. Gating the
                                // whole call on createdCount instead would leave a parent permanently wrong
                                // when an earlier request created rows and then died before updating it: the
                                // waiting requests would all see their own rows, report success, and no
                                // request creating anything would ever be left to trigger the repair.
                                //
                                // Placed before the partial-failure throw below on purpose: rows that did get
                                // created must be reflected in the parent even when other registrations in the
                                // same batch failed.
                                ReconcileParentEffort(parentTask.Id, idContext);

                                // Surfaced only after every registration in the batch has been attempted, not
                                // per-registration: the caller's retry must see one clear failure, but the batch
                                // must not stop partway through. A retry is safe - registrations that already
                                // succeeded are found by RetrieveExistingSubtaskRegistrationIds on the next
                                // attempt and skipped via SubtaskAlreadyExists, so only the still-missing
                                // combinations are retried.
                                if (failures.Count > 0)
                                {
                                    throw new Exception(
                                        SubtaskLockPolicy.BuildPartialFailureMessage(createdCount, registrations.Count, failures),
                                        failures[0]);
                                }

                                TimeSpan duration = DateTime.Now.Subtract(start);
                                if (!targetWasProcessed)
                                {
                                    // Reported ahead of the created/skipped cases on purpose: under
                                    // reconcileAll this request may well have created subtasks for other
                                    // registrations, but the one it was called for was not confirmed and got
                                    // nothing. Saying "Success" here would tell the plug-in its own
                                    // registration was handled when it was not.
                                    result.Message = $"Skipped: NoConfirmedMatchingRegistrationFound. {idContext}. Reconciled other registrations: {createdCount}. Execution time: {duration.ToString(@"hh\:mm\:ss")}";
                                    _logger.LogWarning(result.Message);
                                }
                                else if (createdCount > 0)
                                {
                                    result.Message = $"Success: CreatedSubtasks={createdCount}. {idContext}. Execution time: {duration.ToString(@"hh\:mm\:ss")}";
                                    _logger.LogInformation(result.Message);
                                }
                                else if (SubtaskLockPolicy.AllRegistrationsSkippedAsDuplicates(registrations.Count, createdCount))
                                {
                                    // Distinct from NoConfirmedMatchingRegistrationFound below: registrations
                                    // were found, they just already had subtasks (retry / re-delivered request).
                                    result.Message = $"Skipped: SubtasksAlreadyExist. {idContext}. Execution time: {duration.ToString(@"hh\:mm\:ss")}";
                                    _logger.LogInformation(result.Message);
                                }
                                else
                                {
                                    result.Message = $"Skipped: NoConfirmedMatchingRegistrationFound. {idContext}. Execution time: {duration.ToString(@"hh\:mm\:ss")}";
                                    _logger.LogWarning(result.Message);
                                }
                            }
                        }
                        finally
                        {
                            // ownedLock is only ever non-null here after AcquireParentTaskLock's own Create
                            // call actually succeeded (see below) - never another request's row - so this can
                            // only ever delete a lock this execution acquired itself.
                            if (ownedLock != null && ownedLock.Id != Guid.Empty)
                            {
                                var request = new DeleteRequest()
                                {
                                    Target = ownedLock.ToEntityReference(),
                                    ConcurrencyBehavior = ConcurrencyBehavior.AlwaysOverwrite
                                };
                                this.service.Execute(request);
                                _logger.LogInformation($"Owned lock released. {idContext}. Lock {ownedLock.Id}.");
                            }
                        }
                    }
                }

            }
            return result;
        }

        // Acquires the parent-task lock atomically and returns the lock this execution owns. Returns
        // null only when the wait timed out - no lock was created and there is nothing for the caller to
        // clean up.
        //
        // Atomicity comes from the lock row's primary key, not from the read that precedes the create:
        // the lock's id is set to ParentTaskId itself (see SubtaskLockPolicy.GetDeterministicLockId), so
        // two concurrent executions racing to create it for the same parent task cannot both succeed -
        // Dataverse's own primary-key uniqueness rejects the second Create outright, regardless of what
        // either caller observed on its preceding read. Legacy lock rows created with a random id (from
        // before this change) are still honored: RetrieveActiveLock matches on hso_projecttaskid, not on
        // the row's own id, so an old-style lock is waited on and reclaimed exactly as before.
        private (hso_projecttasklock AcquiredLock, LockWaitOutcome Outcome) AcquireParentTaskLock(
            msdyn_projecttask parentTask,
            int delayExecutionFor,
            string idContext,
            Func<bool> reconciliationComplete)
        {
            TimeSpan lockWaitTimeout = GetLockWaitTimeout();
            DateTime waitStart = DateTime.UtcNow;
            bool waited = false;
            bool delayApplied = false;

            _logger.LogInformation($"Lock acquisition attempted. {idContext}");

            while (true)
            {
                hso_projecttasklock activeLock = RetrieveActiveLock(parentTask.Id, idContext);

                // DelayExecutionFor exists only to wait out Dataverse transaction-commit latency for a
                // Synchronous caller reading its own just-written data. It must never run while the lock
                // is held (see MaxDelayExecutionForMs above), and only ever makes sense once, on the
                // first observation of "no lock yet". When the caller sends 0 -- the only value the
                // corrected, Asynchronous plug-in sends, since it no longer has a visibility gap to wait
                // out -- this block is skipped.
                if (activeLock == null && !delayApplied && delayExecutionFor > 0)
                {
                    delayApplied = true;
                    int delayMs = Math.Min(delayExecutionFor, MaxDelayExecutionForMs);
                    if (delayExecutionFor > MaxDelayExecutionForMs)
                    {
                        _logger.LogWarning($"DelayExecutionFor {delayExecutionFor}ms exceeds cap; using {MaxDelayExecutionForMs}ms instead. {idContext}");
                    }
                    Thread.Sleep(delayMs);

                    // Re-check: another caller may have acquired the lock while we were asleep.
                    activeLock = RetrieveActiveLock(parentTask.Id, idContext);
                }

                if (activeLock != null)
                {
                    if (!waited)
                    {
                        waited = true;
                        _logger.LogInformation($"Lock contention detected. {idContext}. Existing lock record: {activeLock.Id}. Waiting up to {lockWaitTimeout} for release.");
                    }

                    // The lock holder reconciles every confirmed registration under this Parent Task,
                    // so the work this request exists to do is very likely being done right now by
                    // whoever holds the lock. Checking each poll lets this request finish the moment
                    // its own subtask appears instead of queueing for a lock it no longer needs -
                    // this is what drains the N-1 losing requests of a burst.
                    if (reconciliationComplete())
                    {
                        _logger.LogInformation(
                            $"Reconciled by a concurrent request while waiting for the lock. {idContext}. "
                            + $"Waited {(DateTime.UtcNow - waitStart).ToString(@"hh\:mm\:ss")}.");
                        return (null, LockWaitOutcome.TargetRegistrationAlreadyCreated);
                    }

                    if (SubtaskLockPolicy.HasWaitTimedOut(waitStart, DateTime.UtcNow, lockWaitTimeout))
                    {
                        // One last look before reporting failure: the holder may have created this
                        // registration's subtask between the poll above and the timeout. Reporting
                        // ParentTaskLockTimeout when the work is in fact done would fail a System Job
                        // for nothing.
                        if (reconciliationComplete())
                        {
                            _logger.LogInformation(
                                $"Reconciled by a concurrent request as the lock wait expired. {idContext}.");
                            return (null, LockWaitOutcome.TargetRegistrationAlreadyCreated);
                        }

                        _logger.LogWarning($"Lock timeout: ParentTaskLockTimeout. {idContext}. Lock {activeLock.Id} still held after {lockWaitTimeout}.");
                        return (null, LockWaitOutcome.TimedOut);
                    }

                    _logger.LogInformation($"Waiting for lock. {idContext}. Lock {activeLock.Id}.");
                    Thread.Sleep(LockPollInterval);
                    continue;
                }

                // No active lock observed. Attempt the atomic acquisition itself.
                hso_projecttasklock newLock = new hso_projecttasklock();
                newLock.Id = SubtaskLockPolicy.GetDeterministicLockId(parentTask.Id);
                newLock.hso_projecttaskid = parentTask.ToEntityReference();

                try
                {
                    service.Create(newLock);
                }
                catch (Exception ex) when (IsDuplicateKeyFault(ex))
                {
                    // Another request won the deterministic-id race. Treat the duplicate-key response
                    // itself as authoritative contention: during delete/recreate propagation, a fresh
                    // RetrieveMultiple can temporarily return no lock even though the uniqueness check
                    // still rejects this Create. Waiting and retrying is safe and remains bounded by the
                    // configured lock timeout.
                    waited = true;

                    // Same early exit as the polling branch above: the request that won the
                    // deterministic-id race reconciles this registration too, so it may already be done.
                    if (reconciliationComplete())
                    {
                        _logger.LogInformation(
                            $"Reconciled by the concurrent request that won the lock race. {idContext}.");
                        return (null, LockWaitOutcome.TargetRegistrationAlreadyCreated);
                    }

                    if (SubtaskLockPolicy.HasWaitTimedOut(waitStart, DateTime.UtcNow, lockWaitTimeout))
                    {
                        _logger.LogWarning(
                            $"Lock timeout after duplicate-key contention. {idContext}. "
                            + $"Waited {(DateTime.UtcNow - waitStart).ToString(@"hh\:mm\:ss")}.");
                        return (null, LockWaitOutcome.TimedOut);
                    }

                    _logger.LogWarning(
                        ex,
                        $"Transient duplicate-key contention while acquiring parent-task lock. "
                        + $"The request will wait and retry. {idContext}");

                    Thread.Sleep(LockPollInterval);
                    continue;
                }
                catch (Exception ex)
                {
                    // Permissions, connectivity, validation, and every non-duplicate Dataverse fault
                    // remain real failures and must not be hidden behind the contention retry loop.
                    _logger.LogError(ex, $"Lock acquisition failed with an unrelated Dataverse fault. {idContext}");
                    throw;
                }

                if (waited)
                {
                    _logger.LogInformation($"Lock acquired after waiting {(DateTime.UtcNow - waitStart).ToString(@"hh\:mm\:ss")}. {idContext}");
                }
                else
                {
                    _logger.LogInformation($"Lock acquired. {idContext}");
                }

                return (newLock, LockWaitOutcome.Acquired);
            }
        }

        // How a request stopped trying to take the parent-task lock. TargetRegistrationAlreadyCreated
        // is a success for the caller, not a failure: it means a concurrent request reconciled this
        // registration while this one waited, so there is nothing left to do and no lock to release.
        private enum LockWaitOutcome
        {
            Acquired,
            TimedOut,
            TargetRegistrationAlreadyCreated
        }

        // Targeted existence check backing the pre-lock and poll-time early exits. Deliberately not
        // RetrieveExistingSubtaskRegistrationIds: that pulls every child of the parent task, and this
        // runs on every poll of every waiting request.
        private bool RegistrationHasSubtask(Guid parentTaskId, Guid registrationId)
        {
            var fetchXml = $@"<fetch top='1'>
                              <entity name='msdyn_projecttask'>
                                <attribute name='msdyn_projecttaskid' />
                                <filter type='and'>
                                  <condition attribute='msdyn_parenttask' operator='eq' value='{parentTaskId}' />
                                  <condition attribute='ccl_registrationid' operator='eq' value='{registrationId}' />
                                </filter>
                              </entity>
                            </fetch>";

            return service.RetrieveMultiple(new FetchExpression(fetchXml)).Entities.Count > 0;
        }

        private bool GetReconcileAllRegistrations()
        {
            return SubtaskLockPolicy.ResolveReconcileAllRegistrations(_configuration[ReconcileAllRegistrationsConfigKey]);
        }

        private static bool IsDuplicateKeyFault(Exception exception)
        {
            Exception currentException = exception;

            while (currentException != null)
            {
                if (currentException is FaultException<OrganizationServiceFault> fault
                    && ContainsOrganizationServiceErrorCode(fault.Detail, DuplicateKeyErrorCode))
                {
                    return true;
                }

                currentException = currentException.InnerException;
            }

            return false;
        }

        private static bool ContainsOrganizationServiceErrorCode(
            OrganizationServiceFault fault,
            int expectedErrorCode)
        {
            OrganizationServiceFault currentFault = fault;

            while (currentFault != null)
            {
                if (currentFault.ErrorCode == expectedErrorCode)
                {
                    return true;
                }

                currentFault = currentFault.InnerFault;
            }

            return false;
        }

        private TimeSpan GetLockWaitTimeout()
        {
            return SubtaskLockPolicy.ResolveLockWaitTimeout(_configuration[LockWaitTimeoutConfigKey], DefaultLockWaitTimeout);
        }

        // Fresh Dataverse read (service.RetrieveMultiple, never localContext) so every poll attempt sees
        // the current state instead of a potentially tracked/cached entity. Folds in the stale-lock
        // self-heal (see StaleLockThreshold above) -- reclaiming a lock nobody is coming back to release.
        private hso_projecttasklock RetrieveActiveLock(Guid parentTaskId, string idContext)
        {
            // Loops rather than returning immediately after reclaiming a stale lock: a legacy random-id
            // lock and the deterministic lock can in principle coexist briefly for the same parent task,
            // so one delete does not guarantee the parent task is actually unlocked. Each iteration is a
            // fresh Dataverse read, never a cached/tracked entity.
            while (true)
            {
                var fetchXml = $@"<fetch>
                                  <entity name='hso_projecttasklock'>
                                    <attribute name='hso_projecttasklockid' />
                                    <attribute name='hso_projecttaskid' />
                                    <attribute name='createdon' />
                                    <filter type='and'>
                                      <condition attribute='hso_projecttaskid' operator='eq' value='{parentTaskId}' />
                                    </filter>
                                    <order attribute='createdon' descending='true' />
                                  </entity>
                                </fetch>";

                EntityCollection locks = service.RetrieveMultiple(new FetchExpression(fetchXml));
                hso_projecttasklock found = locks.Entities.Select(e => e.ToEntity<hso_projecttasklock>()).FirstOrDefault();

                if (found == null)
                {
                    return null;
                }

                if (!SubtaskLockPolicy.IsLockStale(found.CreatedOn, DateTime.UtcNow, StaleLockThreshold))
                {
                    return found;
                }

                _logger.LogWarning($"Stale lock reclaimed. {idContext}. Lock {found.Id} created {found.CreatedOn:o} exceeded {StaleLockThreshold}; deleting and retrying.");
                try
                {
                    service.Execute(new DeleteRequest
                    {
                        Target = found.ToEntityReference(),
                        ConcurrencyBehavior = ConcurrencyBehavior.AlwaysOverwrite
                    });
                }
                catch (FaultException<OrganizationServiceFault> fault) when (fault.Detail.ErrorCode == ObjectDoesNotExistErrorCode)
                {
                    // A concurrent run reclaimed the same stale lock first and already deleted it.
                    // The desired end state -- lock gone -- was reached, just not by us. Reporting
                    // this as a Failure would reintroduce the misleading-signal class this pass
                    // exists to remove. Only this specific fault is swallowed; anything else
                    // (permissions, connectivity) still propagates.
                    _logger.LogInformation($"Reclaimed: StaleLockAlreadyGone. {idContext}. Lock {found.Id} was deleted by a concurrent run before this one's delete landed.");
                }

                // Loop back for a fresh read instead of assuming "no active lock": another row (from the
                // acquisition race, or created by a concurrent request since this read) may still exist.
            }
        }

        // Brings the parent task effort in line with the complete current set of direct child tasks.
        // The value is derived from Dataverse every time rather than incremented, which makes retries
        // and duplicate-safe re-deliveries idempotent. Callers invoke this while holding the
        // deterministic ParentTaskId lock.
        //
        // Writes only when the stored value actually differs. That is what makes the rollup
        // self-healing: any request that acquires the lock repairs a parent left stale by an earlier
        // request that died between creating its subtask rows and updating the parent, at the cost of
        // two reads. It also preserves the reason this stopped being called unconditionally - the
        // Update fires CclCrmPlugin.ProjectTaskPostUpdateFDD025 synchronously inside the lock, so it
        // must not run when nothing changed.
        private void ReconcileParentEffort(Guid parentTaskId, string idContext)
        {
            double childEffortSum = SumChildTaskEffort(parentTaskId, out int childTaskCount);
            double parentEffort = RetrieveParentEffort(parentTaskId);

            if (SubtaskLockPolicy.ParentEffortMatchesChildren(parentEffort, childEffortSum))
            {
                _logger.LogInformation(
                    $"Parent task effort already consistent; no update issued. ParentTaskId={parentTaskId}, "
                    + $"ChildTaskCount={childTaskCount}, TotalEffort={childEffortSum}. {idContext}");

                return;
            }

            Entity parentUpdate = new Entity("msdyn_projecttask", parentTaskId);
            parentUpdate["msdyn_effort"] = childEffortSum;
            service.Update(parentUpdate);

            _logger.LogInformation(
                $"Parent task effort recalculated. ParentTaskId={parentTaskId}, "
                + $"ChildTaskCount={childTaskCount}, PreviousEffort={parentEffort}, "
                + $"TotalEffort={childEffortSum}. {idContext}");
        }

        private double SumChildTaskEffort(Guid parentTaskId, out int childTaskCount)
        {
            var query = new QueryExpression("msdyn_projecttask")
            {
                ColumnSet = new ColumnSet("msdyn_effort")
            };

            query.Criteria.AddCondition(
                "msdyn_parenttask",
                ConditionOperator.Equal,
                parentTaskId);

            EntityCollection childTasks = service.RetrieveMultiple(query);
            childTaskCount = childTasks.Entities.Count;

            return childTasks.Entities.Sum(
                childTask => childTask.GetAttributeValue<double?>("msdyn_effort") ?? 0d);
        }

        // Fresh read rather than the parentTask entity loaded at the top of CreateSubtasks: a waiting
        // request needs to see the rollup another request just wrote, not the value it read minutes ago.
        private double RetrieveParentEffort(Guid parentTaskId)
        {
            Entity parent = service.Retrieve(
                "msdyn_projecttask",
                parentTaskId,
                new ColumnSet("msdyn_effort"));

            return parent.GetAttributeValue<double?>("msdyn_effort") ?? 0d;
        }

        // The condition a waiting request uses to decide it can stop waiting and report success.
        //
        // Deliberately not "my subtask row exists". A request that exits on its own row alone can
        // report success while the reconciliation that created it is still in flight - and if that
        // reconciliation then dies before updating the parent rollup, every waiter has already
        // returned success and no request is left to finish the parent. The rollup would stay wrong
        // permanently, because a later request that creates nothing has nothing to trigger a repair.
        //
        // So the condition is the durable end state of the whole reconciliation: this registration's
        // subtask exists AND the parent effort already reflects the full child set. The cheap check is
        // evaluated first and short-circuits, so the two rollup reads only happen once the row is
        // actually there.
        private bool ParentReconciliationComplete(Guid parentTaskId, Guid registrationId)
        {
            if (!RegistrationHasSubtask(parentTaskId, registrationId))
            {
                return false;
            }

            double childEffortSum = SumChildTaskEffort(parentTaskId, out _);

            return SubtaskLockPolicy.ParentEffortMatchesChildren(
                RetrieveParentEffort(parentTaskId),
                childEffortSum);
        }

        // Fresh Dataverse read backing the idempotency check: a subtask already exists for a registration
        // when msdyn_projecttask has both msdyn_parenttask = parentTaskId and ccl_registrationid = that
        // registration's id. Returns the set of registration ids that already have a subtask so the
        // per-registration decision (SubtaskLockPolicy.SubtaskAlreadyExists) can run without a query per
        // registration.
        private HashSet<Guid> RetrieveExistingSubtaskRegistrationIds(Guid parentTaskId)
        {
            var fetchXml = $@"<fetch>
                              <entity name='msdyn_projecttask'>
                                <attribute name='msdyn_projecttaskid' />
                                <attribute name='ccl_registrationid' />
                                <filter type='and'>
                                  <condition attribute='msdyn_parenttask' operator='eq' value='{parentTaskId}' />
                                </filter>
                              </entity>
                            </fetch>";

            EntityCollection existing = service.RetrieveMultiple(new FetchExpression(fetchXml));

            return existing.Entities
                .Select(e => e.ToEntity<msdyn_projecttask>().ccl_RegistrationID?.Id)
                .Where(id => id.HasValue)
                .Select(id => id.Value)
                .ToHashSet();
        }

        private CreateResponse DupSubTaskRequest(msdyn_projecttask subTask)
        {
            try
            {
                var request = new CreateRequest();
                request.Target = subTask;
                request.Parameters.Add("SuppressDuplicateDetection", false);//based on entity published Duplicate Detection Rules

                CreateResponse response = (CreateResponse)service.Execute(request);

                _logger.LogInformation($"Subtask created. SubtaskId={response.id}, WBSID={subTask.msdyn_WBSID}, ParentTaskId={subTask.msdyn_parenttask.Id}, SessionRegistrationId={subTask.ccl_RegistrationID.Id}, ParticipantContactId={subTask.ccl_Participant?.Id}");

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Subtask creation failed. ParentTaskId={subTask.msdyn_parenttask.Id}, SessionRegistrationId={subTask.ccl_RegistrationID.Id}, Reason={ex.Message}");
                throw;
            }

        }
    }
}