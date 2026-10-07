using System.Collections.Generic;
using CclWebApi.Helpers;
using Xunit;

namespace CclWebApiCore.Tests;

public class SubtaskLockPolicyTests
{
    // Regression for the "ParentTaskLocked" bug (QA 2026-07-21): two concurrent CreateSubtasks
    // requests for different Session Registrations under the same Parent Task must both eventually
    // create a subtask instead of the second one dropping its work. These tests cover the pure
    // decision logic SubtaskCreator relies on to wait, time out, and de-duplicate.

    [Fact]
    public void GetDeterministicLockId_SameParentTaskId_ReturnsSameLockId()
    {
        var parentTaskId = Guid.NewGuid();

        var first = SubtaskLockPolicy.GetDeterministicLockId(parentTaskId);
        var second = SubtaskLockPolicy.GetDeterministicLockId(parentTaskId);

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetDeterministicLockId_DifferentParentTaskIds_ReturnDifferentLockIds()
    {
        var first = SubtaskLockPolicy.GetDeterministicLockId(Guid.NewGuid());
        var second = SubtaskLockPolicy.GetDeterministicLockId(Guid.NewGuid());

        Assert.NotEqual(first, second);
    }

    // Regression for the 2-participant x 3-parent-task QA scenario: 3 eligible Parent Tasks under one
    // Project must each get their own, non-colliding lock id, so a request against Task A can never be
    // blocked behind - or mistaken for - a lock legitimately held for Task B or Task C.
    [Fact]
    public void GetDeterministicLockId_ThreeEligibleParentTasks_AllLockIdsDistinct()
    {
        var parentTaskA = Guid.NewGuid();
        var parentTaskB = Guid.NewGuid();
        var parentTaskC = Guid.NewGuid();

        var lockIds = new[]
        {
            SubtaskLockPolicy.GetDeterministicLockId(parentTaskA),
            SubtaskLockPolicy.GetDeterministicLockId(parentTaskB),
            SubtaskLockPolicy.GetDeterministicLockId(parentTaskC)
        };

        Assert.Equal(3, new HashSet<Guid>(lockIds).Count);
    }

    // Regression for the exact QA repro: 2 confirmed registrations x 3 eligible parent tasks must
    // produce exactly 6 subtasks - one per (ParentTaskId, SessionRegistrationId) combination - with no
    // combination created twice and none silently dropped. Models what SubtaskCreator.CreateSubtasks
    // does per parent task: a fresh existing-registrations set, checked and grown per registration.
    [Fact]
    public void SubtaskAlreadyExists_TwoRegistrationsAcrossThreeParentTasks_CreatesExactlySixUniqueCombinations()
    {
        var registrationA = Guid.NewGuid();
        var registrationB = Guid.NewGuid();
        var registrations = new[] { registrationA, registrationB };
        var parentTasks = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var created = new HashSet<(Guid ParentTaskId, Guid RegistrationId)>();

        foreach (var parentTaskId in parentTasks)
        {
            // Each parent task's existing-subtask set is independent, exactly as
            // RetrieveExistingSubtaskRegistrationIds scopes its fetch to one parentTaskId.
            var existingForThisParentTask = new HashSet<Guid>();

            foreach (var registrationId in registrations)
            {
                if (SubtaskLockPolicy.SubtaskAlreadyExists(existingForThisParentTask, registrationId))
                {
                    continue;
                }

                created.Add((parentTaskId, registrationId));
                existingForThisParentTask.Add(registrationId);
            }
        }

        Assert.Equal(6, created.Count);
        foreach (var parentTaskId in parentTasks)
        {
            foreach (var registrationId in registrations)
            {
                Assert.Contains((parentTaskId, registrationId), created);
            }
        }
    }

    // Retry-while-locked: a caller polling on a fixed interval must keep retrying (not time out) on
    // every poll before the configured timeout elapses, and must stop retrying (time out) once it does -
    // exactly the sequence AcquireParentTaskLock's wait loop drives via repeated RetrieveActiveLock +
    // HasWaitTimedOut checks while another request owns the deterministic lock.
    [Fact]
    public void HasWaitTimedOut_PollSequenceWhileLockOwnedByAnotherRequest_RetriesUntilTimeout()
    {
        var waitStart = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        var pollInterval = TimeSpan.FromSeconds(2);
        var timeout = TimeSpan.FromSeconds(5);

        var now = waitStart;
        var timedOutPolls = new List<bool>();

        for (int poll = 0; poll < 4; poll++)
        {
            now += pollInterval;
            timedOutPolls.Add(SubtaskLockPolicy.HasWaitTimedOut(waitStart, now, timeout));
        }

        // Polls at +2s and +4s: still within the 5s timeout, keep waiting for the other request's lock.
        Assert.False(timedOutPolls[0]);
        Assert.False(timedOutPolls[1]);
        // Poll at +6s: exceeds the 5s timeout, stop retrying.
        Assert.True(timedOutPolls[2]);
        Assert.True(timedOutPolls[3]);
    }

    // Regression for the Guid.Empty sweep path (ProjectTaskPostUpdateFixed -> one Parent Task, multiple
    // confirmed registrations in a single API request): registration 2 failing must not prevent
    // registration 3 from being attempted, and must not stop registration 1's already-successful create
    // from counting. Mirrors the per-parent-task isolation fix in SessionRegistrationPostCUFixed, applied
    // here to SubtaskCreator's own per-registration loop.
    [Fact]
    public void ProcessRegistrations_MiddleRegistrationFails_RemainingRegistrationsStillAttemptedAndSucceed()
    {
        var registration1 = Guid.NewGuid();
        var registration2 = Guid.NewGuid();
        var registration3 = Guid.NewGuid();
        var registrations = new[] { registration1, registration2, registration3 };
        var existing = new HashSet<Guid>();
        var attempted = new List<Guid>();

        var (createdCount, failures) = SubtaskLockPolicy.ProcessRegistrations(
            registrations,
            id => id,
            existing,
            id =>
            {
                attempted.Add(id);
                if (id == registration2)
                {
                    throw new InvalidOperationException("Simulated Dataverse failure for registration 2.");
                }
            });

        // All three were attempted, in order - registration 2 failing did not stop registration 3.
        Assert.Equal(new[] { registration1, registration2, registration3 }, attempted);

        // Registrations 1 and 3 succeeded; registration 2 is recorded as a failure, not silently dropped.
        Assert.Equal(2, createdCount);
        Assert.Single(failures);
        Assert.Contains(registration1, existing);
        Assert.Contains(registration3, existing);
        Assert.DoesNotContain(registration2, existing);
    }

    // The overall execution must report the partial failure - not a bare "Success", and not silently
    // swallow it - so CreateSubtasksRequestFixed.IsAcceptableOutcome on the plug-in side (and the caller's
    // retry logic) correctly treats this as a failure requiring a retry.
    [Fact]
    public void BuildPartialFailureMessage_ReportsCreatedAndFailedCounts()
    {
        var failures = new List<Exception> { new InvalidOperationException("Simulated Dataverse failure.") };

        var message = SubtaskLockPolicy.BuildPartialFailureMessage(2, 3, failures);

        Assert.Contains("CreatedSubtasks=2 of 3", message);
        Assert.Contains("FailedRegistrations=1", message);
        Assert.Contains("Simulated Dataverse failure.", message);
        Assert.DoesNotContain("Success", message);
        Assert.False(message.StartsWith("Skipped: SubtasksAlreadyExist"));
    }

    // Retry behavior: a second ProcessRegistrations call over the same batch, using the existing-ids set
    // as it would be after a fresh RetrieveExistingSubtaskRegistrationIds read post-failure, must only
    // re-attempt the registration that actually failed - registrations 1 and 3 are already accounted for
    // and must not be recreated.
    [Fact]
    public void ProcessRegistrations_RetryAfterPartialFailure_OnlyReattemptsPreviouslyFailedRegistration()
    {
        var registration1 = Guid.NewGuid();
        var registration2 = Guid.NewGuid();
        var registration3 = Guid.NewGuid();
        var registrations = new[] { registration1, registration2, registration3 };
        var existing = new HashSet<Guid>();
        bool failRegistration2 = true;

        var firstAttempted = new List<Guid>();
        SubtaskLockPolicy.ProcessRegistrations(
            registrations,
            id => id,
            existing,
            id =>
            {
                firstAttempted.Add(id);
                if (id == registration2 && failRegistration2)
                {
                    throw new InvalidOperationException("Simulated failure.");
                }
            });

        Assert.Equal(new[] { registration1, registration2, registration3 }, firstAttempted);
        Assert.Equal(new HashSet<Guid> { registration1, registration3 }, existing);

        // Retry: the underlying issue is fixed, and the existing-ids set reflects what a fresh Dataverse
        // read would now show (registrations 1 and 3 already have a subtask; registration 2 still doesn't).
        failRegistration2 = false;
        var retryAttempted = new List<Guid>();
        var (retryCreatedCount, retryFailures) = SubtaskLockPolicy.ProcessRegistrations(
            registrations,
            id => id,
            existing,
            id => retryAttempted.Add(id));

        Assert.Equal(new[] { registration2 }, retryAttempted);
        Assert.Equal(1, retryCreatedCount);
        Assert.Empty(retryFailures);
        Assert.Equal(new HashSet<Guid> { registration1, registration2, registration3 }, existing);
    }

    [Fact]
    public void IsLockStale_NoCreatedOn_ReturnsFalse()
    {
        Assert.False(SubtaskLockPolicy.IsLockStale(null, DateTime.UtcNow, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void IsLockStale_WithinThreshold_ReturnsFalse()
    {
        var now = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        var createdOn = now - TimeSpan.FromMinutes(5);

        Assert.False(SubtaskLockPolicy.IsLockStale(createdOn, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void IsLockStale_ExceedsThreshold_ReturnsTrue()
    {
        var now = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        var createdOn = now - TimeSpan.FromMinutes(11);

        Assert.True(SubtaskLockPolicy.IsLockStale(createdOn, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void HasWaitTimedOut_BeforeTimeout_ReturnsFalse()
    {
        var waitStart = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        var now = waitStart + TimeSpan.FromSeconds(19);

        Assert.False(SubtaskLockPolicy.HasWaitTimedOut(waitStart, now, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void HasWaitTimedOut_AtOrPastTimeout_ReturnsTrue()
    {
        var waitStart = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        var now = waitStart + TimeSpan.FromSeconds(20);

        Assert.True(SubtaskLockPolicy.HasWaitTimedOut(waitStart, now, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void SubtaskAlreadyExists_RegistrationNotInSet_ReturnsFalse()
    {
        var existing = new[] { Guid.NewGuid(), Guid.NewGuid() };

        Assert.False(SubtaskLockPolicy.SubtaskAlreadyExists(existing, Guid.NewGuid()));
    }

    [Fact]
    public void SubtaskAlreadyExists_RegistrationInSet_ReturnsTrue()
    {
        var registrationId = Guid.NewGuid();
        var existing = new[] { Guid.NewGuid(), registrationId };

        Assert.True(SubtaskLockPolicy.SubtaskAlreadyExists(existing, registrationId));
    }

    [Fact]
    public void SubtaskAlreadyExists_EmptySet_ReturnsFalse()
    {
        Assert.False(SubtaskLockPolicy.SubtaskAlreadyExists(Array.Empty<Guid>(), Guid.NewGuid()));
    }

    [Fact]
    public void AllRegistrationsSkippedAsDuplicates_NoRegistrationsFound_ReturnsFalse()
    {
        // registrationCount == 0 is a different outcome (NoConfirmedMatchingRegistrationFound), not this one.
        Assert.False(SubtaskLockPolicy.AllRegistrationsSkippedAsDuplicates(0, 0));
    }

    [Fact]
    public void AllRegistrationsSkippedAsDuplicates_SomeCreated_ReturnsFalse()
    {
        Assert.False(SubtaskLockPolicy.AllRegistrationsSkippedAsDuplicates(3, 1));
    }

    [Fact]
    public void AllRegistrationsSkippedAsDuplicates_RegistrationsFoundButNoneCreated_ReturnsTrue()
    {
        Assert.True(SubtaskLockPolicy.AllRegistrationsSkippedAsDuplicates(2, 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    public void ResolveLockWaitTimeout_InvalidOrNonPositive_ReturnsDefault(string? configuredSeconds)
    {
        var result = SubtaskLockPolicy.ResolveLockWaitTimeout(configuredSeconds, TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(20), result);
    }

    [Fact]
    public void ResolveLockWaitTimeout_ValidPositiveValue_UsesConfiguredValue()
    {
        var result = SubtaskLockPolicy.ResolveLockWaitTimeout("45", TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(45), result);
    }

    // The parent rollup comparison is what a waiting request uses to decide the reconciliation it is
    // waiting on is durably finished, so "not yet equal" must never read as "done".

    [Theory]
    [InlineData(30d, 30d)]
    [InlineData(0d, 0d)]
    [InlineData(32d, 32.00000001d)]
    public void ParentEffortMatchesChildren_EqualWithinTolerance_ReturnsTrue(double parentEffort, double childSum)
    {
        Assert.True(SubtaskLockPolicy.ParentEffortMatchesChildren(parentEffort, childSum));
    }

    // The exact UAT shape: 15 of 16 subtasks created leaves the parent at 30 while the child set is
    // really 32. A waiter seeing 30 must keep waiting, not report the batch complete.
    [Theory]
    [InlineData(30d, 32d)]
    [InlineData(0d, 2d)]
    [InlineData(18d, 32d)]
    public void ParentEffortMatchesChildren_StaleRollup_ReturnsFalse(double parentEffort, double childSum)
    {
        Assert.False(SubtaskLockPolicy.ParentEffortMatchesChildren(parentEffort, childSum));
    }

    // Reconcile-the-whole-parent-task is the change that collapses the concurrent-request storm, so an
    // unreadable or missing config value must leave it ON. Only an explicit "false" turns it off.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-bool")]
    [InlineData("1")]
    public void ResolveReconcileAllRegistrations_UnsetOrUnparseable_StaysEnabled(string? configuredValue)
    {
        Assert.True(SubtaskLockPolicy.ResolveReconcileAllRegistrations(configuredValue));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void ResolveReconcileAllRegistrations_ExplicitValue_IsHonoured(string configuredValue, bool expected)
    {
        Assert.Equal(expected, SubtaskLockPolicy.ResolveReconcileAllRegistrations(configuredValue));
    }

    // WBS child numbering for a batch of subtasks under one parent task. The Guid.Empty sweep creates
    // every confirmed registration's subtask in a tight loop, so deriving each number from a fresh
    // Dataverse read would both add a round trip inside the parent-task lock and depend on the row
    // just created already being visible to the next read.

    [Fact]
    public void TakeChildNumber_NotYetSeeded_UsesSeedAndAdvances()
    {
        var result = SubtaskLockPolicy.TakeChildNumber(null, () => 7);

        Assert.Equal(7, result.ChildNumber);
        Assert.Equal(8, result.NextChildNumber);
    }

    [Fact]
    public void TakeChildNumber_AlreadySeeded_DoesNotCallSeedAgain()
    {
        var result = SubtaskLockPolicy.TakeChildNumber(
            12,
            () => throw new Xunit.Sdk.XunitException("seed must not be re-read once the counter is seeded"));

        Assert.Equal(12, result.ChildNumber);
        Assert.Equal(13, result.NextChildNumber);
    }

    // The regression this guards: 16 registrations under one Parent Task must receive 16 distinct,
    // consecutive WBS child numbers from a single seed read, not 16 numbers derived from 16 separate
    // reads that can each miss the preceding insert.
    [Fact]
    public void TakeChildNumber_SixteenRegistrationBatch_ProducesConsecutiveDistinctNumbersFromOneSeed()
    {
        int seedCalls = 0;
        int? next = null;
        var issued = new List<int>();

        for (int i = 0; i < 16; i++)
        {
            var result = SubtaskLockPolicy.TakeChildNumber(next, () => { seedCalls++; return 1; });
            next = result.NextChildNumber;
            issued.Add(result.ChildNumber);
        }

        Assert.Equal(1, seedCalls);
        Assert.Equal(16, issued.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 16), issued);
    }
}
