namespace QuestionableCompanion.Services;

internal enum RetainerBatchLifecycleResult
{
	Complete,
	ProcessedWithFailures,
	Cancelled,
	Suspended,
	UnsafeCleanup,
	TerminalFailure
}
