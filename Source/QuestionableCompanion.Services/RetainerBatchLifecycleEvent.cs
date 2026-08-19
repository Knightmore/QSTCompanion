namespace QuestionableCompanion.Services;

internal enum RetainerBatchLifecycleEvent
{
	NormalCompletion,
	ProcessedWithFailures,
	UnsafeCleanup,
	TerminalConflict,
	ExplicitCancellationCompleted,
	Expired,
	Malformed,
	DefinitiveIdentityMismatch,
	Disposal
}
