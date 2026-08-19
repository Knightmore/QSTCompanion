namespace QuestionableCompanion.Services;

internal static class RetainerNamingSessionOutcomeLogic
{
	public static bool RequiresOuterRecovery(RetainerNamingSessionOutcome outcome)
	{
		return outcome == RetainerNamingSessionOutcome.Failed;
	}

	public static bool MustStopWithoutOuterRecovery(RetainerNamingSessionOutcome outcome)
	{
		if (outcome == RetainerNamingSessionOutcome.AcceptedClosureUnverified || outcome == RetainerNamingSessionOutcome.ClosureUnverified)
		{
			return true;
		}
		return false;
	}

	public static bool PreservesAcceptedSideEffect(RetainerNamingSessionOutcome outcome)
	{
		if ((uint)outcome <= 1u)
		{
			return true;
		}
		return false;
	}

	public static bool CanAdvanceAfterVerifiedClosure(RetainerNamingSessionOutcome outcome)
	{
		return outcome == RetainerNamingSessionOutcome.Exhausted;
	}
}
