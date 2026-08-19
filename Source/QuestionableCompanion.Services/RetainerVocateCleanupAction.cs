namespace QuestionableCompanion.Services;

internal enum RetainerVocateCleanupAction
{
	ConfirmInputCancellation,
	RequestInputCancellation,
	WaitForInputCancellation,
	DirectCloseResidualInputString,
	ContinueCleanup
}
