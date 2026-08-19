using System.Collections.Generic;

namespace QuestionableCompanion.Services;

public sealed class JobStoneGearsetReconciliationCoordinator
{
	private JobStoneGearsetTarget? target;

	private JobStoneGearsetObservation? lastStableObservation;

	private JobStoneGearsetObservation? terminalObservation;

	private JobStoneGearsetReconciliationResult? terminalResult;

	private JobStoneGearsetDecisionKind? lastMutationKind;

	private int stableReads;

	private int mutationAttempts;

	public JobStoneGearsetTarget? CurrentTarget => target;

	public JobStoneGearsetReconciliationResult Observe(JobStoneGearsetObservation observation, IReadOnlyList<CombatJobDefinition> definitions)
	{
		JobStoneTargetResolution jobStoneTargetResolution = JobStoneGearsetReconciliationLogic.ResolveTarget(observation, definitions);
		if (jobStoneTargetResolution.Kind != JobStoneTargetResolutionKind.Exact || jobStoneTargetResolution.Target == null)
		{
			JobStoneGearsetTarget jobStoneGearsetTarget = target;
			Reset();
			return new JobStoneGearsetReconciliationResult((jobStoneGearsetTarget != null) ? JobStoneGearsetReconciliationStatus.Cancelled : ((jobStoneTargetResolution.Kind != JobStoneTargetResolutionKind.NotApplicable) ? JobStoneGearsetReconciliationStatus.Deferred : JobStoneGearsetReconciliationStatus.NotApplicable), jobStoneGearsetTarget, null, 0, 0, jobStoneTargetResolution.Reason);
		}
		if (target != jobStoneTargetResolution.Target)
		{
			Reset();
			target = jobStoneTargetResolution.Target;
		}
		if (!JobStoneGearsetReconciliationLogic.IsMutationSafe(observation, target, out string reason))
		{
			ResetStableReads();
			return Result(JobStoneGearsetReconciliationStatus.Deferred, null, reason);
		}
		if (terminalResult != null && terminalObservation != null)
		{
			if (JobStoneGearsetReconciliationLogic.Equivalent(terminalObservation, observation))
			{
				return terminalResult;
			}
			terminalResult = null;
			terminalObservation = null;
			mutationAttempts = 0;
			lastMutationKind = null;
			ResetStableReads();
		}
		if (lastStableObservation != null && JobStoneGearsetReconciliationLogic.Equivalent(lastStableObservation, observation))
		{
			stableReads++;
		}
		else
		{
			lastStableObservation = observation;
			stableReads = 1;
		}
		if (stableReads < 4)
		{
			return Result(JobStoneGearsetReconciliationStatus.Stabilizing, null, $"waiting for stable read {stableReads}/{4}");
		}
		JobStoneGearsetDecision jobStoneGearsetDecision = JobStoneGearsetReconciliationLogic.Decide(target, observation.ActiveGearsetId, observation.Gearsets, definitions);
		if (jobStoneGearsetDecision.Kind == JobStoneGearsetDecisionKind.PreserveExisting)
		{
			JobStoneGearsetReconciliationResult result = Result(lastMutationKind switch
			{
				JobStoneGearsetDecisionKind.UpdateActive => JobStoneGearsetReconciliationStatus.Updated, 
				JobStoneGearsetDecisionKind.CreateNew => JobStoneGearsetReconciliationStatus.Created, 
				_ => JobStoneGearsetReconciliationStatus.Preserved, 
			}, jobStoneGearsetDecision, jobStoneGearsetDecision.Reason);
			mutationAttempts = 0;
			lastMutationKind = null;
			return result;
		}
		if (jobStoneGearsetDecision.Kind == JobStoneGearsetDecisionKind.FullCapacity)
		{
			return SetTerminal(JobStoneGearsetReconciliationStatus.FullCapacity, jobStoneGearsetDecision, observation);
		}
		if (mutationAttempts >= 3)
		{
			return SetTerminal(JobStoneGearsetReconciliationStatus.RetryExhausted, jobStoneGearsetDecision, observation, $"gearset mutation was not verified after {mutationAttempts} attempts");
		}
		return Result(JobStoneGearsetReconciliationStatus.MutationPending, jobStoneGearsetDecision, jobStoneGearsetDecision.Reason);
	}

	public void RecordMutationAttempt(JobStoneGearsetTarget attemptedTarget, JobStoneGearsetDecisionKind mutationKind)
	{
		bool flag = target != attemptedTarget;
		if (!flag)
		{
			bool flag2 = (uint)(mutationKind - 1) <= 1u;
			flag = !flag2;
		}
		if (!flag)
		{
			mutationAttempts++;
			lastMutationKind = mutationKind;
			ResetStableReads();
		}
	}

	public void DeferCurrentObservation()
	{
		ResetStableReads();
	}

	public void Reset()
	{
		target = null;
		mutationAttempts = 0;
		lastMutationKind = null;
		terminalObservation = null;
		terminalResult = null;
		ResetStableReads();
	}

	private JobStoneGearsetReconciliationResult SetTerminal(JobStoneGearsetReconciliationStatus status, JobStoneGearsetDecision decision, JobStoneGearsetObservation observation, string? reason = null)
	{
		terminalObservation = observation;
		terminalResult = Result(status, decision, reason ?? decision.Reason);
		return terminalResult;
	}

	private JobStoneGearsetReconciliationResult Result(JobStoneGearsetReconciliationStatus status, JobStoneGearsetDecision? decision, string reason)
	{
		return new JobStoneGearsetReconciliationResult(status, target, decision, stableReads, mutationAttempts, reason);
	}

	private void ResetStableReads()
	{
		lastStableObservation = null;
		stableReads = 0;
	}
}
