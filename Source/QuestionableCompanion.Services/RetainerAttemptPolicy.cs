namespace QuestionableCompanion.Services;

public static class RetainerAttemptPolicy
{
	public const int MaximumAttempts = 3;

	public static bool CanRetry(int completedAttempts, bool terminalFailure)
	{
		if (!terminalFailure)
		{
			return completedAttempts < 3;
		}
		return false;
	}
}
