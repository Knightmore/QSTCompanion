namespace QuestionableCompanion.Services;

internal static class RetainerNamingAttemptLogic
{
	public static RetainerNamingAttemptDecision Decide(bool acceptedCandidateInNativeRoster, bool inputStringReady, bool vocateEventOccupied, bool finalCandidate)
	{
		if (acceptedCandidateInNativeRoster)
		{
			return RetainerNamingAttemptDecision.Accepted;
		}
		if (!finalCandidate)
		{
			if (!(inputStringReady && vocateEventOccupied))
			{
				return RetainerNamingAttemptDecision.StructuralFailure;
			}
			return RetainerNamingAttemptDecision.RetrySameEvent;
		}
		if (inputStringReady && vocateEventOccupied)
		{
			return RetainerNamingAttemptDecision.CloseExhaustedSession;
		}
		if (!inputStringReady && !vocateEventOccupied)
		{
			return RetainerNamingAttemptDecision.VerifyExhaustedSessionClosure;
		}
		return RetainerNamingAttemptDecision.StructuralFailure;
	}
}
