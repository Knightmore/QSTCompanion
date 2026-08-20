namespace QuestionableCompanion.Services;

internal static class RetainerNativeCapacityLogic
{
	public static RetainerNativeCapacityPlan Plan(int vocateCurrentCount, int vocateMaximumCount, RetainerNativeRosterSnapshot native, int liveRosterCount, int baselineCount, int trackedCount, int persistedIntendedCount)
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
		if (baselineCount < 0 || trackedCount < 0 || baselineCount + trackedCount > native.CurrentCount)
		{
			return Invalid("Baseline and Companion-owned retainer counts are inconsistent with the stable native roster.");
		}
		int num = native.MaximumCount - baselineCount;
		int num2 = ((persistedIntendedCount == 0) ? num : persistedIntendedCount);
		if (num2 > num)
		{
			return Invalid("The checkpoint's intended Companion-owned retainer count exceeds the slots left by the baseline roster.");
		}
		if (trackedCount > num2)
		{
			return Invalid("Tracked retainer ownership is inconsistent with the stable native entitlement.");
		}
		int num3 = num2 - trackedCount;
		int num4 = native.MaximumCount - native.CurrentCount;
		if (num3 > num4)
		{
			return Invalid("Unrelated live retainers consume slots reserved by this partial checkpoint; they will not be modified.");
		}
		return new RetainerNativeCapacityPlan(IsValid: true, num2, num3, num4, string.Empty);
	}

	private static RetainerNativeCapacityPlan Invalid(string error)
	{
		return new RetainerNativeCapacityPlan(IsValid: false, 0, 0, 0, error);
	}
}
