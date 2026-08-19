namespace QuestionableCompanion.Services;

public sealed record RetainerNativeRosterObservation(bool IsAvailable, int CurrentCount, int MaximumCount, int RosterCount, string RosterFingerprint)
{
	public RetainerNativeRosterSnapshot ToSnapshot()
	{
		return new RetainerNativeRosterSnapshot(CurrentCount, MaximumCount, RosterCount, RosterFingerprint ?? string.Empty);
	}
}
