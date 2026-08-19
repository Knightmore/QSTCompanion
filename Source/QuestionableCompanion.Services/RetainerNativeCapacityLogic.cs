namespace QuestionableCompanion.Services;

internal static class RetainerNativeCapacityLogic
{
	public static RetainerNativeCapacityPlan Plan(int vocateCurrentCount, int vocateMaximumCount, RetainerNativeRosterSnapshot native, int liveRosterCount, int trackedCount, int persistedIntendedCount)
	{
		if (native.MaximumCount <= 0)
		{
			return Invalid("The live client reports no retainer entitlement.");
		}
		if (native.CurrentCount < 0 || native.CurrentCount > native.MaximumCount || native.CurrentCount != native.RosterCount || native.RosterCount != liveRosterCount)
		{
			return Invalid("Native retainer entitlement and roster counts did not agree; the roster will not be modified.");
		}
		if (vocateCurrentCount != native.CurrentCount || vocateMaximumCount != native.MaximumCount)
		{
			return Invalid("Native retainer entitlement changed between the Vocate interaction and the stable roster read.");
		}
		int num = ((persistedIntendedCount == 0) ? native.MaximumCount : persistedIntendedCount);
		if (num > native.MaximumCount)
		{
			return Invalid("The checkpoint's intended retainer count exceeds the current native entitlement.");
		}
		if (trackedCount < 0 || trackedCount > native.CurrentCount || trackedCount > num)
		{
			return Invalid("Tracked retainer ownership is inconsistent with the stable native entitlement.");
		}
		int num2 = num - trackedCount;
		int num3 = native.MaximumCount - native.CurrentCount;
		if (num2 > num3)
		{
			return Invalid("Unrelated live retainers consume slots reserved by this partial checkpoint; they will not be modified.");
		}
		return new RetainerNativeCapacityPlan(IsValid: true, num, num2, num3, string.Empty);
	}

	private static RetainerNativeCapacityPlan Invalid(string error)
	{
		return new RetainerNativeCapacityPlan(IsValid: false, 0, 0, 0, error);
	}
}
