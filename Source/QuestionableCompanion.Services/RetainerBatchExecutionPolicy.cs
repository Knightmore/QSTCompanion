namespace QuestionableCompanion.Services;

public static class RetainerBatchExecutionPolicy
{
	public static RetainerBatchDisposition AfterCharacter(bool cancellationRequested, bool cleanupVerified)
	{
		if (!cleanupVerified)
		{
			return RetainerBatchDisposition.TerminateUnsafe;
		}
		if (!cancellationRequested)
		{
			return RetainerBatchDisposition.Continue;
		}
		return RetainerBatchDisposition.CancelledCleanly;
	}
}
