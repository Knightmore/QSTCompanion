namespace QuestionableCompanion.Models;

public enum RetainerCheckpointDisposition
{
	Unclassified,
	Complete,
	ResumablePartial,
	RetryablePreSideEffectFailure,
	InterruptedBeforeSideEffects,
	UnsafeOrTerminal
}
