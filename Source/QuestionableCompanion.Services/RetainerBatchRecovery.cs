namespace QuestionableCompanion.Services;

public static class RetainerBatchRecovery
{
	public static RetainerCleanupDecision Decide(bool abortSettled, bool resetAlreadyIssued, bool autoRetainerIdle, bool relevantWindowsClosed)
	{
		if (autoRetainerIdle && relevantWindowsClosed)
		{
			return RetainerCleanupDecision.ContinueBatch;
		}
		if (!abortSettled && !resetAlreadyIssued)
		{
			return RetainerCleanupDecision.IssueSingleReset;
		}
		return RetainerCleanupDecision.TerminateBatch;
	}
}
