using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace QuestionableCompanion.Services;

public sealed class JobStoneGearsetReconciliationService : IDisposable
{
	private const int SoulCrystalEquippedSlot = 13;

	private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(250L);

	private static readonly TimeSpan RequestedReconciliationTimeout = TimeSpan.FromSeconds(12L);

	private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(30L);

	private readonly CombatJobResolver combatJobResolver;

	private readonly IFramework framework;

	private readonly IClientState clientState;

	private readonly ICondition condition;

	private readonly IPlayerState playerState;

	private readonly IObjectTable objectTable;

	private readonly IPluginLog log;

	private readonly JobStoneGearsetReconciliationCoordinator coordinator = new JobStoneGearsetReconciliationCoordinator();

	private DateTime nextObservationUtc = DateTime.MinValue;

	private DateTime nextErrorLogUtc = DateTime.MinValue;

	private JobStoneGearsetTarget? verifiedTarget;

	private int verifiedGearsetId = -1;

	private string lastWarningKey = string.Empty;

	private bool disposed;

	private JobStoneGearsetReconciliationResult latestResult = new JobStoneGearsetReconciliationResult(JobStoneGearsetReconciliationStatus.Deferred, null, null, 0, 0, "reconciliation has not observed a character yet");

	public JobStoneGearsetReconciliationService(CombatJobResolver combatJobResolver, IFramework framework, IClientState clientState, ICondition condition, IPlayerState playerState, IObjectTable objectTable, IPluginLog log)
	{
		this.combatJobResolver = combatJobResolver;
		this.framework = framework;
		this.clientState = clientState;
		this.condition = condition;
		this.playerState = playerState;
		this.objectTable = objectTable;
		this.log = log;
		framework.Update += OnFrameworkUpdate;
	}

	public async Task<JobStoneGearsetReconciliationResult> ReconcileCurrentAsync(string context, CancellationToken cancellationToken)
	{
		JobStoneGearsetReconciliationResult jobStoneGearsetReconciliationResult = await framework.RunOnFrameworkThread(delegate
		{
			if (disposed)
			{
				return DisposedResult();
			}
			try
			{
				ProcessObservationUnsafe(force: true);
				JobStoneGearsetObservation observation = ReadObservationUnsafe();
				JobStoneTargetResolution jobStoneTargetResolution = JobStoneGearsetReconciliationLogic.ResolveTarget(observation, combatJobResolver.Definitions);
				if (jobStoneTargetResolution.Kind != JobStoneTargetResolutionKind.Exact || jobStoneTargetResolution.Target == null)
				{
					return new JobStoneGearsetReconciliationResult((jobStoneTargetResolution.Kind != JobStoneTargetResolutionKind.NotApplicable) ? JobStoneGearsetReconciliationStatus.Deferred : JobStoneGearsetReconciliationStatus.NotApplicable, null, null, 0, 0, jobStoneTargetResolution.Reason);
				}
				if (IsVerifiedTargetUnsafe(observation, jobStoneTargetResolution.Target))
				{
					return VerifiedResult(jobStoneTargetResolution.Target, verifiedGearsetId, "exact gearset was already verified");
				}
				return (latestResult.Target == jobStoneTargetResolution.Target) ? latestResult : new JobStoneGearsetReconciliationResult(JobStoneGearsetReconciliationStatus.Stabilizing, jobStoneTargetResolution.Target, null, 0, 0, "waiting for the requested target's stable observations");
			}
			catch (Exception ex)
			{
				HandleObservationException(ex);
				return latestResult;
			}
		});
		if (jobStoneGearsetReconciliationResult.IsTerminal || jobStoneGearsetReconciliationResult.Target == null)
		{
			LogRequestedOutcome(context, jobStoneGearsetReconciliationResult);
			return jobStoneGearsetReconciliationResult;
		}
		JobStoneGearsetTarget requestedTarget = jobStoneGearsetReconciliationResult.Target;
		DateTime deadline = DateTime.UtcNow + RequestedReconciliationTimeout;
		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			JobStoneGearsetReconciliationResult jobStoneGearsetReconciliationResult2 = await framework.RunOnFrameworkThread(delegate
			{
				if (disposed)
				{
					return DisposedResult();
				}
				try
				{
					ProcessObservationUnsafe(force: false);
					JobStoneGearsetObservation observation = ReadObservationUnsafe();
					JobStoneTargetResolution jobStoneTargetResolution = JobStoneGearsetReconciliationLogic.ResolveTarget(observation, combatJobResolver.Definitions);
					if (jobStoneTargetResolution.Kind != JobStoneTargetResolutionKind.Exact || jobStoneTargetResolution.Target != requestedTarget)
					{
						return new JobStoneGearsetReconciliationResult(JobStoneGearsetReconciliationStatus.Cancelled, requestedTarget, null, 0, 0, (jobStoneTargetResolution.Kind == JobStoneTargetResolutionKind.Exact) ? "character identity, job, or soul crystal changed" : jobStoneTargetResolution.Reason);
					}
					if (IsVerifiedTargetUnsafe(observation, requestedTarget))
					{
						return VerifiedResult(requestedTarget, verifiedGearsetId, "exact gearset is verified");
					}
					return (latestResult.Target == requestedTarget) ? latestResult : new JobStoneGearsetReconciliationResult(JobStoneGearsetReconciliationStatus.Stabilizing, requestedTarget, null, 0, 0, "waiting for the requested target's stable observations");
				}
				catch (Exception ex)
				{
					HandleObservationException(ex);
					return latestResult;
				}
			});
			if (jobStoneGearsetReconciliationResult2.IsTerminal)
			{
				LogRequestedOutcome(context, jobStoneGearsetReconciliationResult2);
				return jobStoneGearsetReconciliationResult2;
			}
			await Task.Delay(100, cancellationToken);
		}
		JobStoneGearsetReconciliationResult result = new JobStoneGearsetReconciliationResult(JobStoneGearsetReconciliationStatus.Deferred, requestedTarget, null, latestResult.StableReads, latestResult.MutationAttempts, $"timed out after {RequestedReconciliationTimeout.TotalSeconds:F0}s waiting for a safe stable state");
		LogRequestedOutcome(context, result);
		return result;
	}

	public async Task<JobStoneGearsetDemotionGuard> GetDemotionGuardAsync(uint destinationClassJobId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return await framework.RunOnFrameworkThread(delegate
		{
			if (disposed)
			{
				return new JobStoneGearsetDemotionGuard(Suppress: false, null, "reconciliation service is disposed");
			}
			try
			{
				JobStoneGearsetObservation observation = ReadObservationUnsafe();
				JobStoneTargetResolution jobStoneTargetResolution = JobStoneGearsetReconciliationLogic.ResolveTarget(observation, combatJobResolver.Definitions);
				if (jobStoneTargetResolution.Kind != JobStoneTargetResolutionKind.Exact || jobStoneTargetResolution.Target == null)
				{
					return new JobStoneGearsetDemotionGuard(Suppress: false, null, jobStoneTargetResolution.Reason);
				}
				bool persistenceSucceeded = IsVerifiedTargetUnsafe(observation, jobStoneTargetResolution.Target);
				bool flag = JobStoneGearsetReconciliationLogic.ShouldSuppressBaseClassDemotion(jobStoneTargetResolution.Target, destinationClassJobId, persistenceSucceeded, combatJobResolver.Definitions);
				return new JobStoneGearsetDemotionGuard(flag, jobStoneTargetResolution.Target, flag ? "the selected gearset would demote the exact live promoted job to its same-experience base class before persistence succeeded" : string.Empty);
			}
			catch (Exception ex)
			{
				HandleObservationException(ex);
				JobStoneGearsetTarget currentTarget = coordinator.CurrentTarget;
				bool flag2 = currentTarget != null && JobStoneGearsetReconciliationLogic.ShouldSuppressBaseClassDemotion(currentTarget, destinationClassJobId, persistenceSucceeded: false, combatJobResolver.Definitions);
				return new JobStoneGearsetDemotionGuard(flag2, currentTarget, flag2 ? "live verification failed while a matching promoted-job persistence target remained pending" : latestResult.Reason);
			}
		});
	}

	public async Task<CurrentGearsetPersistenceResult> PersistCurrentGearsetAsync(string context, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		CurrentGearsetPersistenceResult currentGearsetPersistenceResult = await framework.RunOnFrameworkThread((Func<CurrentGearsetPersistenceResult>)PersistCurrentGearsetUnsafe);
		string messageTemplate = $"[Gearsets] {context}: success={currentGearsetPersistenceResult.Success}, classJob={currentGearsetPersistenceResult.ClassJobId}, gearset={currentGearsetPersistenceResult.GearsetId + 1}, created={currentGearsetPersistenceResult.Created}, reason={currentGearsetPersistenceResult.Reason}.";
		if (currentGearsetPersistenceResult.Success)
		{
			log.Information(messageTemplate);
		}
		else
		{
			log.Warning(messageTemplate);
		}
		return currentGearsetPersistenceResult;
	}

	private unsafe CurrentGearsetPersistenceResult PersistCurrentGearsetUnsafe()
	{
		if (disposed)
		{
			return GearsetPersistenceFailure("service is disposed");
		}
		JobStoneGearsetObservation observation = ReadObservationUnsafe();
		if (!observation.IsLoggedIn || !observation.DalamudPlayerStateLoaded || !observation.NativePlayerStateLoaded)
		{
			return GearsetPersistenceFailure("player state is not loaded");
		}
		if (observation.DalamudContentId == 0L || observation.DalamudContentId != observation.NativeContentId || observation.NativeContentId != observation.GearsetContentId)
		{
			return GearsetPersistenceFailure("player and gearset ContentIds do not match");
		}
		if (observation.NativeClassJobId == 0 || observation.NativeClassJobId != observation.DalamudClassJobId)
		{
			return GearsetPersistenceFailure("current class/job is not stable");
		}
		if (!observation.EquippedItemsLoaded || !observation.GearsetDataAvailable || observation.GearsetIsVirtual || !observation.SafeToMutate)
		{
			return GearsetPersistenceFailure("gearset mutation is not safe or its data is unavailable");
		}
		RaptureGearsetModule* ptr = RaptureGearsetModule.Instance();
		if (ptr == null)
		{
			return GearsetPersistenceFailure("RaptureGearsetModule is unavailable");
		}
		uint nativeClassJobId = observation.NativeClassJobId;
		JobStoneGearsetState jobStoneGearsetState = observation.Gearsets.FirstOrDefault((JobStoneGearsetState jobStoneGearsetState2) => jobStoneGearsetState2.Exists && jobStoneGearsetState2.GearsetId == observation.ActiveGearsetId);
		bool flag = jobStoneGearsetState == null || jobStoneGearsetState.ClassJobId != nativeClassJobId;
		int num;
		if (flag)
		{
			num = ptr->FirstEmptyGearsetSlot();
			if ((num < 0 || num >= 100) ? true : false)
			{
				return GearsetPersistenceFailure("all 100 gearset slots are occupied", nativeClassJobId);
			}
			int num2 = ptr->CreateGearset();
			if (num2 != num)
			{
				return GearsetPersistenceFailure($"CreateGearset returned {num2}, expected {num}", nativeClassJobId);
			}
		}
		else
		{
			num = jobStoneGearsetState.GearsetId;
			ptr->UpdateGearset(num);
		}
		RaptureGearsetModule.GearsetEntry* gearset = ptr->GetGearset(num);
		if (gearset == null || (gearset->Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0 || gearset->ClassJob != nativeClassJobId)
		{
			return GearsetPersistenceFailure("saved gearset did not retain the current class/job", nativeClassJobId);
		}
		uint itemId = gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.SoulStone).ItemId;
		if (itemId != observation.EquippedSoulCrystalItemId)
		{
			return GearsetPersistenceFailure($"saved soul crystal {itemId} did not match equipped {observation.EquippedSoulCrystalItemId}", nativeClassJobId);
		}
		if (!SavedItemsMatchEquippedUnsafe(gearset, out string mismatch))
		{
			return GearsetPersistenceFailure("saved equipment did not match the current loadout (" + mismatch + ")", nativeClassJobId);
		}
		return new CurrentGearsetPersistenceResult(Success: true, num, nativeClassJobId, flag, flag ? "created a new exact gearset" : "updated the matching active gearset");
	}

	private unsafe static bool SavedItemsMatchEquippedUnsafe(RaptureGearsetModule.GearsetEntry* gearset, out string mismatch)
	{
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null)
		{
			mismatch = "InventoryManager unavailable";
			return false;
		}
		InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(InventoryType.EquippedItems);
		if (inventoryContainer == null || !inventoryContainer->IsLoaded)
		{
			mismatch = "equipped inventory unavailable";
			return false;
		}
		for (int i = 0; i <= 13; i++)
		{
			InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(i);
			uint num = ((inventorySlot != null) ? inventorySlot->ItemId : 0u);
			uint itemId = gearset->GetItem((RaptureGearsetModule.GearsetItemIndex)i).ItemId;
			if (NormalizeInventoryItemId(itemId) != NormalizeInventoryItemId(num))
			{
				mismatch = $"slot {i}: saved {itemId}, equipped {num}";
				return false;
			}
		}
		mismatch = string.Empty;
		return true;
	}

	private static uint NormalizeInventoryItemId(uint itemId)
	{
		return itemId % 1000000;
	}

	private static CurrentGearsetPersistenceResult GearsetPersistenceFailure(string reason, uint classJobId = 0u)
	{
		return new CurrentGearsetPersistenceResult(Success: false, -1, classJobId, Created: false, reason);
	}

	private void OnFrameworkUpdate(IFramework _)
	{
		if (disposed)
		{
			return;
		}
		try
		{
			ProcessObservationUnsafe(force: false);
		}
		catch (Exception ex)
		{
			HandleObservationException(ex);
		}
	}

	private unsafe void ProcessObservationUnsafe(bool force)
	{
		DateTime utcNow = DateTime.UtcNow;
		if (!force && utcNow < nextObservationUtc)
		{
			return;
		}
		nextObservationUtc = utcNow + ObservationInterval;
		try
		{
			JobStoneGearsetObservation jobStoneGearsetObservation = ReadObservationUnsafe();
			JobStoneTargetResolution jobStoneTargetResolution = JobStoneGearsetReconciliationLogic.ResolveTarget(jobStoneGearsetObservation, combatJobResolver.Definitions);
			if (jobStoneTargetResolution.Kind != JobStoneTargetResolutionKind.Exact || jobStoneTargetResolution.Target == null || verifiedTarget != jobStoneTargetResolution.Target || !HasExactGearset(jobStoneGearsetObservation, jobStoneTargetResolution.Target, verifiedGearsetId))
			{
				verifiedTarget = null;
				verifiedGearsetId = -1;
			}
			latestResult = coordinator.Observe(jobStoneGearsetObservation, combatJobResolver.Definitions);
			if (latestResult.PersistenceSucceeded && latestResult.Target != null && latestResult.Decision != null)
			{
				verifiedTarget = latestResult.Target;
				verifiedGearsetId = latestResult.Decision.GearsetId;
				lastWarningKey = string.Empty;
				return;
			}
			JobStoneGearsetReconciliationStatus status = latestResult.Status;
			if ((uint)(status - 7) <= 1u)
			{
				LogTerminalWarning(latestResult);
			}
			else
			{
				if (latestResult.Status != JobStoneGearsetReconciliationStatus.MutationPending || latestResult.Target == null || latestResult.Decision == null)
				{
					return;
				}
				JobStoneGearsetObservation jobStoneGearsetObservation2 = ReadObservationUnsafe();
				JobStoneTargetResolution jobStoneTargetResolution2 = JobStoneGearsetReconciliationLogic.ResolveTarget(jobStoneGearsetObservation2, combatJobResolver.Definitions);
				if (jobStoneTargetResolution2.Kind != JobStoneTargetResolutionKind.Exact || jobStoneTargetResolution2.Target != latestResult.Target || !JobStoneGearsetReconciliationLogic.IsMutationSafe(jobStoneGearsetObservation2, latestResult.Target, out string _) || !JobStoneGearsetReconciliationLogic.Equivalent(jobStoneGearsetObservation, jobStoneGearsetObservation2))
				{
					latestResult = coordinator.Observe(jobStoneGearsetObservation2, combatJobResolver.Definitions);
					return;
				}
				JobStoneGearsetDecision jobStoneGearsetDecision = JobStoneGearsetReconciliationLogic.Decide(latestResult.Target, jobStoneGearsetObservation2.ActiveGearsetId, jobStoneGearsetObservation2.Gearsets, combatJobResolver.Definitions);
				if (jobStoneGearsetDecision != latestResult.Decision)
				{
					latestResult = coordinator.Observe(jobStoneGearsetObservation2, combatJobResolver.Definitions);
					return;
				}
				RaptureGearsetModule* ptr = RaptureGearsetModule.Instance();
				if (ptr == null || ptr->CharacterContentId != latestResult.Target.ContentId || ptr->IsVirtual)
				{
					coordinator.DeferCurrentObservation();
					return;
				}
				if (jobStoneGearsetDecision.Kind == JobStoneGearsetDecisionKind.CreateNew && ptr->FirstEmptyGearsetSlot() != jobStoneGearsetDecision.GearsetId)
				{
					coordinator.DeferCurrentObservation();
					return;
				}
				int value = latestResult.MutationAttempts + 1;
				int value2;
				try
				{
					value2 = jobStoneGearsetDecision.Kind switch
					{
						JobStoneGearsetDecisionKind.UpdateActive => ptr->UpdateGearset(jobStoneGearsetDecision.GearsetId), 
						JobStoneGearsetDecisionKind.CreateNew => ptr->CreateGearset(), 
						_ => throw new InvalidOperationException($"Unexpected reconciliation decision {jobStoneGearsetDecision.Kind}."), 
					};
				}
				catch (Exception ex)
				{
					value2 = int.MinValue;
					log.Warning($"[JobStoneGearsets] Native {jobStoneGearsetDecision.Kind} attempt {value}/{3} raised an error: {ex.Message}");
				}
				coordinator.RecordMutationAttempt(latestResult.Target, jobStoneGearsetDecision.Kind);
				log.Information($"[JobStoneGearsets] Native {jobStoneGearsetDecision.Kind} attempt {value}/{3}: contentId={latestResult.Target.ContentId}, job={latestResult.Target.ClassJobId}, stone={latestResult.Target.SoulCrystalItemId}, gearset={jobStoneGearsetDecision.GearsetId + 1}, result={value2}. Verifying the exact saved job and stone before any retry.");
				JobStoneGearsetObservation observation = ReadObservationUnsafe();
				latestResult = coordinator.Observe(observation, combatJobResolver.Definitions);
				nextObservationUtc = DateTime.UtcNow + ObservationInterval;
			}
		}
		catch (Exception ex2)
		{
			HandleObservationException(ex2);
		}
	}

	private unsafe JobStoneGearsetObservation ReadObservationUnsafe()
	{
		bool isLoggedIn = clientState.IsLoggedIn;
		bool isLoaded = playerState.IsLoaded;
		ulong dalamudContentId = (isLoaded ? playerState.ContentId : 0);
		uint dalamudClassJobId = (isLoaded ? playerState.ClassJob.RowId : 0u);
		PlayerState* ptr = PlayerState.Instance();
		bool flag = ptr != null && ptr->IsLoaded;
		ulong nativeContentId = (flag ? ptr->ContentId : 0);
		uint nativeClassJobId = (uint)(flag ? ptr->CurrentClassJobId : 0);
		bool equippedItemsLoaded = false;
		uint equippedSoulCrystalItemId = 0u;
		InventoryManager* ptr2 = InventoryManager.Instance();
		if (ptr2 != null)
		{
			InventoryContainer* inventoryContainer = ptr2->GetInventoryContainer(InventoryType.EquippedItems);
			if (inventoryContainer != null && inventoryContainer->IsLoaded)
			{
				equippedItemsLoaded = true;
				InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(13);
				equippedSoulCrystalItemId = ((inventorySlot != null) ? inventorySlot->ItemId : 0u);
			}
		}
		List<JobStoneGearsetState> list = new List<JobStoneGearsetState>(100);
		bool gearsetDataAvailable = false;
		bool gearsetIsVirtual = false;
		ulong gearsetContentId = 0uL;
		int activeGearsetId = -1;
		RaptureGearsetModule* ptr3 = RaptureGearsetModule.Instance();
		if (ptr3 != null)
		{
			gearsetDataAvailable = true;
			gearsetIsVirtual = ptr3->IsVirtual;
			gearsetContentId = ptr3->CharacterContentId;
			activeGearsetId = ptr3->CurrentGearsetIndex;
			for (int i = 0; i < 100; i++)
			{
				RaptureGearsetModule.GearsetEntry* gearset = ptr3->GetGearset(i);
				if (gearset == null)
				{
					gearsetDataAvailable = false;
					list.Add(new JobStoneGearsetState(i, Exists: false, 0u, 0u));
				}
				else
				{
					bool flag2 = (gearset->Flags & RaptureGearsetModule.GearsetFlag.Exists) != 0;
					uint soulCrystalItemId = (flag2 ? gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.SoulStone).ItemId : 0u);
					list.Add(new JobStoneGearsetState(i, flag2, (uint)(flag2 ? gearset->ClassJob : 0), soulCrystalItemId));
				}
			}
		}
		return new JobStoneGearsetObservation(isLoggedIn, isLoaded, flag, dalamudContentId, nativeContentId, gearsetContentId, dalamudClassJobId, nativeClassJobId, equippedItemsLoaded, equippedSoulCrystalItemId, gearsetDataAvailable, gearsetIsVirtual, IsSafeToMutateUnsafe(), activeGearsetId, list);
	}

	private bool IsSafeToMutateUnsafe()
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (!clientState.IsLoggedIn || localPlayer == null || !playerState.IsLoaded || playerState.ContentId == 0L)
		{
			return false;
		}
		if (condition[ConditionFlag.Unconscious] || localPlayer.CurrentHp == 0 || localPlayer.IsDead)
		{
			return false;
		}
		if (condition[ConditionFlag.InCombat] || condition[ConditionFlag.BoundByDuty])
		{
			return false;
		}
		if (condition[ConditionFlag.Casting] || localPlayer.IsCasting)
		{
			return false;
		}
		if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.InFlight])
		{
			return false;
		}
		if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut])
		{
			return false;
		}
		if (!condition[ConditionFlag.Occupied] && !condition[ConditionFlag.Occupied30] && !condition[ConditionFlag.OccupiedInEvent] && !condition[ConditionFlag.OccupiedInQuestEvent] && !condition[ConditionFlag.Occupied33] && !condition[ConditionFlag.OccupiedInCutSceneEvent] && !condition[ConditionFlag.Occupied38] && !condition[ConditionFlag.Occupied39] && !condition[ConditionFlag.WatchingCutscene])
		{
			return !condition[ConditionFlag.WatchingCutscene78];
		}
		return false;
	}

	private bool IsVerifiedTargetUnsafe(JobStoneGearsetObservation observation, JobStoneGearsetTarget target)
	{
		if (verifiedTarget == target)
		{
			return HasExactGearset(observation, target, verifiedGearsetId);
		}
		return false;
	}

	private static bool HasExactGearset(JobStoneGearsetObservation observation, JobStoneGearsetTarget target, int preferredGearsetId)
	{
		if (observation.GearsetDataAvailable && !observation.GearsetIsVirtual && observation.GearsetContentId == target.ContentId)
		{
			if (preferredGearsetId < 0 || !observation.Gearsets.Exists((JobStoneGearsetState gearset) => gearset.GearsetId == preferredGearsetId && gearset.Exists && gearset.ClassJobId == target.ClassJobId && gearset.SoulCrystalItemId == target.SoulCrystalItemId))
			{
				return observation.Gearsets.Exists((JobStoneGearsetState gearset) => gearset.Exists && gearset.ClassJobId == target.ClassJobId && gearset.SoulCrystalItemId == target.SoulCrystalItemId);
			}
			return true;
		}
		return false;
	}

	private static JobStoneGearsetReconciliationResult VerifiedResult(JobStoneGearsetTarget target, int gearsetId, string reason)
	{
		return new JobStoneGearsetReconciliationResult(JobStoneGearsetReconciliationStatus.Preserved, target, new JobStoneGearsetDecision(JobStoneGearsetDecisionKind.PreserveExisting, gearsetId, reason), 4, 0, reason);
	}

	private static JobStoneGearsetReconciliationResult DisposedResult()
	{
		return new JobStoneGearsetReconciliationResult(JobStoneGearsetReconciliationStatus.Disposed, null, null, 0, 0, "reconciliation service is disposed");
	}

	private void LogRequestedOutcome(string context, JobStoneGearsetReconciliationResult result)
	{
		if (result.Status != JobStoneGearsetReconciliationStatus.NotApplicable)
		{
			string messageTemplate = $"[JobStoneGearsets] {context}: status={result.Status}, job={result.Target?.ClassJobId ?? 0}, stone={result.Target?.SoulCrystalItemId ?? 0}, gearset={(result.Decision?.GearsetId ?? (-1)) + 1}, reason={result.Reason}.";
			if (result.PersistenceSucceeded)
			{
				log.Information(messageTemplate);
			}
			else
			{
				log.Warning(messageTemplate);
			}
		}
	}

	private void LogTerminalWarning(JobStoneGearsetReconciliationResult result)
	{
		string text = $"{result.Status}:{result.Target}:{result.Decision?.GearsetId}:{result.MutationAttempts}";
		if (!(text == lastWarningKey))
		{
			lastWarningKey = text;
			log.Warning($"[JobStoneGearsets] {result.Status}: contentId={result.Target?.ContentId ?? 0}, job={result.Target?.ClassJobId ?? 0}, stone={result.Target?.SoulCrystalItemId ?? 0}; {result.Reason}. Combat-job batches may continue, but matching base-class demotion is suppressed.");
		}
	}

	private void HandleObservationException(Exception ex)
	{
		coordinator.DeferCurrentObservation();
		latestResult = new JobStoneGearsetReconciliationResult(JobStoneGearsetReconciliationStatus.Deferred, coordinator.CurrentTarget, null, 0, latestResult.MutationAttempts, "runtime observation failed: " + ex.Message);
		if (!(DateTime.UtcNow < nextErrorLogUtc))
		{
			nextErrorLogUtc = DateTime.UtcNow + ErrorLogInterval;
			log.Warning("[JobStoneGearsets] Runtime observation deferred: " + ex.Message);
		}
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			framework.Update -= OnFrameworkUpdate;
			coordinator.Reset();
			verifiedTarget = null;
			verifiedGearsetId = -1;
			latestResult = DisposedResult();
		}
	}
}
