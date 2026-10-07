using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CclWebApi.Helpers
{
    // Pure decision logic pulled out of SubtaskCreator so it can be unit tested without a live
    // Dataverse connection. No CRM calls here - only timestamp/collection arithmetic.
    public static class SubtaskLockPolicy
    {
        // The lock record for a given Parent Task always gets the same primary key: the Parent Task's
        // own id. That turns lock acquisition's Create call into an atomic operation - Dataverse's
        // primary-key uniqueness constraint means two concurrent Creates for the same parent task cannot
        // both succeed - without needing a separate alternate key on hso_projecttaskid (none is currently
        // defined on the entity).
        public static Guid GetDeterministicLockId(Guid parentTaskId)
        {
            return parentTaskId;
        }

        public static bool IsLockStale(DateTime? lockCreatedOnUtc, DateTime nowUtc, TimeSpan staleThreshold)
        {
            return lockCreatedOnUtc.HasValue && nowUtc - lockCreatedOnUtc.Value > staleThreshold;
        }

        public static bool HasWaitTimedOut(DateTime waitStartUtc, DateTime nowUtc, TimeSpan timeout)
        {
            return nowUtc - waitStartUtc >= timeout;
        }

        public static bool SubtaskAlreadyExists(IEnumerable<Guid> existingRegistrationIdsForParent, Guid registrationId)
        {
            return existingRegistrationIdsForParent.Contains(registrationId);
        }

        // True when every matching registration was skipped because it already had a subtask - distinct
        // from "no matching registration was found at all" (registrationCount == 0), which is a different
        // outcome and must not share the same result message.
        public static bool AllRegistrationsSkippedAsDuplicates(int registrationCount, int createdCount)
        {
            return registrationCount > 0 && createdCount == 0;
        }

        // Attempts to create a subtask for every registration in the batch, isolating each attempt so
        // one registration's failure (transient Dataverse error, throttling, a duplicate-detection hit,
        // anything) cannot prevent the remaining registrations from being attempted. Mirrors the
        // per-parent-task isolation fix in
        // CclCrmPluginFix.SessionRegistrationPostCUFixed.CreateParticipantSubtasks - same shape, applied
        // here to the API's own registration loop (used for both a single targeted registration and the
        // SessionRegistrationId=Guid.Empty sweep of every confirmed registration under one Parent Task).
        //
        // existingRegistrationIds is mutated in place (a successful create adds to it) so a caller
        // processing multiple batches in the same request sees an up-to-date idempotency snapshot, exactly
        // as SubtaskCreator.CreateSubtasks's single existingSubtaskRegistrationIds set already did before
        // this was extracted.
        public static (int CreatedCount, List<Exception> Failures) ProcessRegistrations<TRegistration>(
            IEnumerable<TRegistration> registrations,
            Func<TRegistration, Guid> getRegistrationId,
            HashSet<Guid> existingRegistrationIds,
            Action<TRegistration> createSubtask,
            Action<TRegistration>? onSkippedAsDuplicate = null)
        {
            int createdCount = 0;
            var failures = new List<Exception>();

            foreach (TRegistration registration in registrations)
            {
                Guid registrationId = getRegistrationId(registration);

                if (SubtaskAlreadyExists(existingRegistrationIds, registrationId))
                {
                    onSkippedAsDuplicate?.Invoke(registration);
                    continue;
                }

                try
                {
                    createSubtask(registration);
                    createdCount++;
                    existingRegistrationIds.Add(registrationId);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            return (createdCount, failures);
        }

        // Single-line summary for the aggregate exception SubtaskCreator throws when
        // ProcessRegistrations reports one or more failures - deliberately excludes "Success" and does
        // not start with "Skipped: SubtasksAlreadyExist", so CreateSubtasksRequestFixed.IsAcceptableOutcome
        // (the plug-in's response parser) correctly rejects a partial failure rather than treating it as
        // a successful no-op.
        public static string BuildPartialFailureMessage(int createdCount, int registrationCount, IReadOnlyList<Exception> failures)
        {
            return $"PartialSubtaskCreationFailure: CreatedSubtasks={createdCount} of {registrationCount}, "
                + $"FailedRegistrations={failures.Count}. FirstError: {failures[0].Message}";
        }

        // Hands out the next WBS child number for a batch of subtasks under one parent task.
        //
        // currentNext is the counter's state: null means "not seeded yet", in which case seed() is
        // invoked to read the starting number from Dataverse. Once seeded, numbers are handed out from
        // memory and seed() is never called again. That matters because seed() is a Dataverse
        // RetrieveMultiple issued while the parent-task lock is held: calling it per registration adds
        // a round trip to the critical section, and it would derive the next number from a read that
        // has to already see the row created moments earlier.
        //
        // Returns the number to use plus the counter's new state, so the caller stores NextChildNumber
        // back. Written as a pure function rather than mutating a field so it can be tested here
        // without a live IOrganizationService.
        public static (int ChildNumber, int NextChildNumber) TakeChildNumber(int? currentNext, Func<int> seed)
        {
            int childNumber = currentNext ?? seed();

            return (childNumber, childNumber + 1);
        }

        // msdyn_effort is a floating-point column, so the parent rollup and the sum of its children are
        // compared with a tolerance rather than for exact equality. Efforts are entered in whole or
        // half hours, so anything this close is the same value re-derived, not a real difference.
        private const double EffortTolerance = 0.0001d;

        // Whether the parent task's stored effort already equals the sum of its child tasks. This is the
        // durable end state of a reconciliation: subtask rows created AND the parent rollup updated to
        // match. A waiting request uses it to decide whether the work it is waiting on is genuinely
        // finished, and a lock holder uses it to decide whether an Update is needed at all.
        public static bool ParentEffortMatchesChildren(double parentEffort, double childEffortSum)
        {
            return Math.Abs(parentEffort - childEffortSum) < EffortTolerance;
        }

        // Whether the request that wins the parent-task lock reconciles every confirmed registration
        // under that Parent Task rather than only the one it was called for. Defaults to enabled: it is
        // what collapses the N-concurrent-requests-per-parent storm into a single unit of work. The
        // config key exists purely as an operator kill switch, so a QA run can fall back to the old
        // one-registration-per-request behaviour without a redeploy. Anything unset, blank, or
        // unparseable keeps the default rather than silently disabling the fix.
        public static bool ResolveReconcileAllRegistrations(string? configuredValue)
        {
            if (!string.IsNullOrWhiteSpace(configuredValue)
                && bool.TryParse(configuredValue, out bool enabled))
            {
                return enabled;
            }

            return true;
        }

        // Parses an optional, operator-configured lock-wait timeout (seconds). Falls back to
        // defaultTimeout when unset, blank, non-numeric, or non-positive, so a bad/missing config value
        // can never produce a zero or unbounded wait.
        public static TimeSpan ResolveLockWaitTimeout(string? configuredSeconds, TimeSpan defaultTimeout)
        {
            if (!string.IsNullOrWhiteSpace(configuredSeconds)
                && double.TryParse(configuredSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return defaultTimeout;
        }
    }
}
