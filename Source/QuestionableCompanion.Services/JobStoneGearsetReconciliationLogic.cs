using System.Collections.Generic;
using System.Linq;

namespace QuestionableCompanion.Services;

public static class JobStoneGearsetReconciliationLogic
{
	public const int RequiredStableReads = 4;

	public const int MaximumMutationAttempts = 3;

	public const int MaximumGearsets = 100;

	public static JobStoneTargetResolution ResolveTarget(JobStoneGearsetObservation observation, IReadOnlyList<CombatJobDefinition> definitions)
	{
		if (!observation.IsLoggedIn)
		{
			return Deferred("client is not logged in");
		}
		if (!observation.DalamudPlayerStateLoaded || !observation.NativePlayerStateLoaded)
		{
			return Deferred("player state is not loaded");
		}
		if (observation.DalamudContentId == 0L || observation.NativeContentId == 0L)
		{
			return Deferred("character ContentId is unavailable");
		}
		if (observation.DalamudContentId != observation.NativeContentId)
		{
			return Deferred("Dalamud and native PlayerState ContentIds do not match");
		}
		if (observation.DalamudClassJobId == 0 || observation.NativeClassJobId == 0)
		{
			return Deferred("live class job is unavailable");
		}
		if (observation.DalamudClassJobId != observation.NativeClassJobId)
		{
			return Deferred("Dalamud and native PlayerState class jobs do not match");
		}
		CombatJobDefinition combatJobDefinition = definitions.FirstOrDefault((CombatJobDefinition definition) => definition.ClassJobId == observation.NativeClassJobId && definition.SoulCrystalItemId != 0);
		if (combatJobDefinition == null)
		{
			return new JobStoneTargetResolution(JobStoneTargetResolutionKind.NotApplicable, null, "the live job is not backed by a soul crystal");
		}
		if (!observation.EquippedItemsLoaded)
		{
			return Deferred("equipped-item data is not loaded");
		}
		if (observation.EquippedSoulCrystalItemId != combatJobDefinition.SoulCrystalItemId)
		{
			return Deferred($"equipped soul crystal {observation.EquippedSoulCrystalItemId} does not match job {combatJobDefinition.ClassJobId} stone {combatJobDefinition.SoulCrystalItemId}");
		}
		return new JobStoneTargetResolution(JobStoneTargetResolutionKind.Exact, new JobStoneGearsetTarget(observation.DalamudContentId, combatJobDefinition.ClassJobId, combatJobDefinition.ExpArrayIndex, combatJobDefinition.SoulCrystalItemId), string.Empty);
	}

	public static bool IsMutationSafe(JobStoneGearsetObservation observation, JobStoneGearsetTarget target, out string reason)
	{
		if (!observation.GearsetDataAvailable)
		{
			reason = "gearset data is unavailable";
			return false;
		}
		if (observation.GearsetIsVirtual)
		{
			reason = "gearset data is virtual";
			return false;
		}
		if (observation.GearsetContentId != target.ContentId)
		{
			reason = $"gearset data belongs to ContentId {observation.GearsetContentId}, expected {target.ContentId}";
			return false;
		}
		if (!HasCompleteGearsetMap(observation.Gearsets))
		{
			reason = "gearset data is incomplete";
			return false;
		}
		if (!observation.SafeToMutate)
		{
			reason = "the character is in an unsafe transition or combat state";
			return false;
		}
		reason = string.Empty;
		return true;
	}

	public static JobStoneGearsetDecision Decide(JobStoneGearsetTarget target, int activeGearsetId, IReadOnlyList<JobStoneGearsetState> gearsets, IReadOnlyList<CombatJobDefinition> definitions)
	{
		JobStoneGearsetState jobStoneGearsetState = (from gearset in gearsets
			where gearset.Exists && gearset.ClassJobId == target.ClassJobId && gearset.SoulCrystalItemId == target.SoulCrystalItemId
			orderby gearset.GearsetId
			select gearset).FirstOrDefault();
		if (jobStoneGearsetState != null)
		{
			return new JobStoneGearsetDecision(JobStoneGearsetDecisionKind.PreserveExisting, jobStoneGearsetState.GearsetId, "an exact promoted-job gearset already exists");
		}
		JobStoneGearsetState jobStoneGearsetState2 = gearsets.FirstOrDefault((JobStoneGearsetState gearset) => gearset.Exists && gearset.GearsetId == activeGearsetId);
		if (jobStoneGearsetState2 != null)
		{
			HashSet<uint> hashSet = (from definition in definitions
				where definition.ExpArrayIndex == target.ExpArrayIndex && definition.SoulCrystalItemId == 0
				select definition.ClassJobId).ToHashSet();
			if (jobStoneGearsetState2.ClassJobId == target.ClassJobId || hashSet.Contains(jobStoneGearsetState2.ClassJobId))
			{
				return new JobStoneGearsetDecision(JobStoneGearsetDecisionKind.UpdateActive, jobStoneGearsetState2.GearsetId, (jobStoneGearsetState2.ClassJobId == target.ClassJobId) ? "the active gearset represents the current promoted job but lacks the exact stone" : "the active gearset represents the promoted job's base class");
			}
		}
		Dictionary<int, JobStoneGearsetState> dictionary = (from gearset in gearsets.Where(delegate(JobStoneGearsetState gearset)
			{
				int gearsetId = gearset.GearsetId;
				return gearsetId >= 0 && gearsetId < 100;
			})
			group gearset by gearset.GearsetId).ToDictionary((IGrouping<int, JobStoneGearsetState> group) => group.Key, (IGrouping<int, JobStoneGearsetState> group) => group.First());
		for (int num = 0; num < 100; num++)
		{
			if (!dictionary.TryGetValue(num, out var value) || !value.Exists)
			{
				return new JobStoneGearsetDecision(JobStoneGearsetDecisionKind.CreateNew, num, "no suitable gearset exists; use the first empty slot");
			}
		}
		return new JobStoneGearsetDecision(JobStoneGearsetDecisionKind.FullCapacity, -1, "all 100 gearset slots are occupied");
	}

	public static bool ShouldSuppressBaseClassDemotion(JobStoneGearsetTarget target, uint destinationClassJobId, bool persistenceSucceeded, IReadOnlyList<CombatJobDefinition> definitions)
	{
		if (persistenceSucceeded || destinationClassJobId == 0)
		{
			return false;
		}
		return definitions.Any((CombatJobDefinition definition) => definition.ClassJobId == destinationClassJobId && definition.ExpArrayIndex == target.ExpArrayIndex && definition.SoulCrystalItemId == 0);
	}

	public static bool Equivalent(JobStoneGearsetObservation left, JobStoneGearsetObservation right)
	{
		if (left.IsLoggedIn != right.IsLoggedIn || left.DalamudPlayerStateLoaded != right.DalamudPlayerStateLoaded || left.NativePlayerStateLoaded != right.NativePlayerStateLoaded || left.DalamudContentId != right.DalamudContentId || left.NativeContentId != right.NativeContentId || left.GearsetContentId != right.GearsetContentId || left.DalamudClassJobId != right.DalamudClassJobId || left.NativeClassJobId != right.NativeClassJobId || left.EquippedItemsLoaded != right.EquippedItemsLoaded || left.EquippedSoulCrystalItemId != right.EquippedSoulCrystalItemId || left.GearsetDataAvailable != right.GearsetDataAvailable || left.GearsetIsVirtual != right.GearsetIsVirtual || left.SafeToMutate != right.SafeToMutate || left.ActiveGearsetId != right.ActiveGearsetId || left.Gearsets.Count != right.Gearsets.Count)
		{
			return false;
		}
		IOrderedEnumerable<JobStoneGearsetState> first = left.Gearsets.OrderBy((JobStoneGearsetState gearset) => gearset.GearsetId);
		IOrderedEnumerable<JobStoneGearsetState> second = right.Gearsets.OrderBy((JobStoneGearsetState gearset) => gearset.GearsetId);
		return first.SequenceEqual(second);
	}

	private static bool HasCompleteGearsetMap(IReadOnlyList<JobStoneGearsetState> gearsets)
	{
		if (gearsets.Count == 100 && gearsets.Select((JobStoneGearsetState gearset) => gearset.GearsetId).Distinct().Count() == 100)
		{
			return gearsets.All(delegate(JobStoneGearsetState gearset)
			{
				int gearsetId = gearset.GearsetId;
				return gearsetId >= 0 && gearsetId < 100;
			});
		}
		return false;
	}

	private static JobStoneTargetResolution Deferred(string reason)
	{
		return new JobStoneTargetResolution(JobStoneTargetResolutionKind.Deferred, null, reason);
	}
}
