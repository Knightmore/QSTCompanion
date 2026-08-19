using System;

namespace QuestionableCompanion.Services;

public static class RetainerVocateFlowLogic
{
	public static bool HasCachedEntitlement(int maximumCount)
	{
		return maximumCount > 0;
	}

	public static bool EntitlementWaitCompleted(int currentCount, int maximumCount, bool hireEntrySelected)
	{
		return (maximumCount > 0 && maximumCount == currentCount) || hireEntrySelected;
	}

	public static bool RequiresProbeDecline(int currentCount, int maximumCount)
	{
		if (maximumCount <= 0)
		{
			throw new ArgumentOutOfRangeException("maximumCount", "Entitlement data must be loaded first.");
		}
		return maximumCount != currentCount;
	}

	public static int OpenSlots(int currentCount, int maximumCount)
	{
		return Math.Max(0, maximumCount - currentCount);
	}
}
