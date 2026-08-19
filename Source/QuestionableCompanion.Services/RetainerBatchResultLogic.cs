namespace QuestionableCompanion.Services;

internal static class RetainerBatchResultLogic
{
	public static RetainerBatchLifecycleResult FromProcessedCounts(int successfulTargets, int totalTargets)
	{
		if (totalTargets <= 0 || successfulTargets != totalTargets)
		{
			return RetainerBatchLifecycleResult.ProcessedWithFailures;
		}
		return RetainerBatchLifecycleResult.Complete;
	}

	public static string PresentationStage(RetainerBatchLifecycleResult result)
	{
		return result switch
		{
			RetainerBatchLifecycleResult.Complete => "Complete", 
			RetainerBatchLifecycleResult.ProcessedWithFailures => "Incomplete", 
			RetainerBatchLifecycleResult.Cancelled => "Cancelled", 
			RetainerBatchLifecycleResult.Suspended => "Suspended", 
			RetainerBatchLifecycleResult.UnsafeCleanup => "Unsafe cleanup", 
			RetainerBatchLifecycleResult.TerminalFailure => "Failed", 
			_ => "Failed", 
		};
	}
}
