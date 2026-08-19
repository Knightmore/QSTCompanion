namespace QuestionableCompanion.Services;

internal sealed record RetainerNamingSessionResult(RetainerNamingSessionOutcome Outcome, LiveRetainerInfo? Retainer, int SubmittedCount, string Error)
{
	public static RetainerNamingSessionResult Accepted(LiveRetainerInfo retainer, int submittedCount)
	{
		return new RetainerNamingSessionResult(RetainerNamingSessionOutcome.Accepted, retainer, submittedCount, string.Empty);
	}

	public static RetainerNamingSessionResult AcceptedClosureUnverified(LiveRetainerInfo retainer, string error, int submittedCount)
	{
		return new RetainerNamingSessionResult(RetainerNamingSessionOutcome.AcceptedClosureUnverified, retainer, submittedCount, error);
	}

	public static RetainerNamingSessionResult Exhausted(int submittedCount)
	{
		return new RetainerNamingSessionResult(RetainerNamingSessionOutcome.Exhausted, null, submittedCount, string.Empty);
	}

	public static RetainerNamingSessionResult ClosureUnverified(string error, int submittedCount)
	{
		return new RetainerNamingSessionResult(RetainerNamingSessionOutcome.ClosureUnverified, null, submittedCount, error);
	}

	public static RetainerNamingSessionResult Failed(string error, int submittedCount = 0)
	{
		return new RetainerNamingSessionResult(RetainerNamingSessionOutcome.Failed, null, submittedCount, error);
	}
}
