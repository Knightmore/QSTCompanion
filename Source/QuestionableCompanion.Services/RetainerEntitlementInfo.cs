using System;

namespace QuestionableCompanion.Services;

public sealed record RetainerEntitlementInfo(int CurrentCount, int MaximumCount)
{
	public int AvailableSlots => Math.Max(0, MaximumCount - CurrentCount);
}
