using System;

namespace QuestionableCompanion.Services;

internal static class RetainerNamingSequenceLogic
{
	public const int SessionCount = 3;

	public const int CandidatesPerSession = 3;

	public const int MaximumSubmittedNames = 9;

	public static RetainerNamingSequenceDecision AfterSession(int sessionIndex, bool accepted)
	{
		if ((sessionIndex < 0 || sessionIndex >= 3) ? true : false)
		{
			throw new ArgumentOutOfRangeException("sessionIndex");
		}
		if (accepted)
		{
			return RetainerNamingSequenceDecision.Complete;
		}
		return sessionIndex switch
		{
			0 => RetainerNamingSequenceDecision.StartReversedSession, 
			1 => RetainerNamingSequenceDecision.StartFreshSession, 
			_ => RetainerNamingSequenceDecision.Fail, 
		};
	}
}
