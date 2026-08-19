using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public static class RetainerBatchRequeuePolicy
{
	public static bool ShouldRequeueAtEnd(CharacterRetainerSetupCheckpoint checkpoint, bool alreadyRequeued, bool cancellationRequested)
	{
		if (!alreadyRequeued && !cancellationRequested)
		{
			return checkpoint.IsRetryablePreSideEffectFailure;
		}
		return false;
	}
}
