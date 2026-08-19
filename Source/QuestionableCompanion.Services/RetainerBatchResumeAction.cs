namespace QuestionableCompanion.Services;

internal enum RetainerBatchResumeAction
{
	WaitForDependencies,
	ResumeCancellationCleanup,
	WaitForTransition,
	ContinueExactTarget,
	IssueRelog,
	WaitForRelogCooldown,
	ClearExpired,
	ClearMalformed,
	ClearIdentityMismatch
}
