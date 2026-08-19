namespace QuestionableCompanion.Services;

internal static class RetainerAcceptedHireCleanupLogic
{
	public static RetainerAcceptedHireCleanupAction Decide(bool inputStringPresent)
	{
		if (!inputStringPresent)
		{
			return RetainerAcceptedHireCleanupAction.ObserveClosure;
		}
		return RetainerAcceptedHireCleanupAction.DirectCloseInputString;
	}
}
