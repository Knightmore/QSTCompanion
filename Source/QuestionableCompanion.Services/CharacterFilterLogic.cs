using System.Collections.Generic;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public static class CharacterFilterLogic
{
	public static int? GetHighestKnownCombatJobLevel(Dictionary<uint, int>? trustedLevels, Dictionary<uint, int>? observedLevels)
	{
		int highestLevel = 0;
		UpdateHighestKnownLevel(trustedLevels, ref highestLevel);
		UpdateHighestKnownLevel(observedLevels, ref highestLevel);
		if (highestLevel <= 0)
		{
			return null;
		}
		return highestLevel;
	}

	public static bool IsLevelRangeValid(CharacterFilterConfiguration filter)
	{
		if (filter.AboveLevelEnabled && filter.BelowLevelEnabled)
		{
			return filter.AboveLevel < filter.BelowLevel;
		}
		return true;
	}

	public static bool MatchesLevel(int? level, CharacterFilterConfiguration filter)
	{
		if (!IsLevelRangeValid(filter))
		{
			return false;
		}
		if (!filter.HasActiveLevelRange)
		{
			return true;
		}
		if ((!level.HasValue || level.GetValueOrDefault() <= 0) ? true : false)
		{
			return false;
		}
		if (filter.AboveLevelEnabled && level <= filter.AboveLevel)
		{
			return false;
		}
		if (filter.BelowLevelEnabled && level >= filter.BelowLevel)
		{
			return false;
		}
		return true;
	}

	public static bool MatchesMissingRetainers(bool filterEnabled, XadbRetainerRosterStatus rosterStatus, CharacterRetainerSetupCheckpoint? checkpoint)
	{
		if (!filterEnabled)
		{
			return true;
		}
		if (checkpoint != null)
		{
			return RetainerSetupLogic.IsEligibleForExplicitRun(checkpoint);
		}
		if ((uint)rosterStatus <= 1u)
		{
			return true;
		}
		return false;
	}

	public static bool MatchesClassUnlock(uint selectedClassJobId, ClassUnlockFilterStatus status, CharacterJobLevelSnapshot? snapshot)
	{
		if (selectedClassJobId == 0 || status == ClassUnlockFilterStatus.All)
		{
			return true;
		}
		if (snapshot == null || !snapshot.HasAllClassJobLevels)
		{
			return status == ClassUnlockFilterStatus.Unknown;
		}
		int value;
		bool flag = snapshot.AllClassJobLevels.TryGetValue(selectedClassJobId, out value) && value > 0;
		return status switch
		{
			ClassUnlockFilterStatus.Unlocked => flag, 
			ClassUnlockFilterStatus.NotUnlocked => !flag, 
			ClassUnlockFilterStatus.Unknown => false, 
			_ => true, 
		};
	}

	private static void UpdateHighestKnownLevel(Dictionary<uint, int>? levels, ref int highestLevel)
	{
		if (levels == null)
		{
			return;
		}
		foreach (KeyValuePair<uint, int> level in levels)
		{
			if (level.Key != 0 && level.Value > highestLevel && level.Value <= 100)
			{
				highestLevel = level.Value;
			}
		}
	}
}
