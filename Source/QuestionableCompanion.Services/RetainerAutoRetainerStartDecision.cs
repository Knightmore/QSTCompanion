namespace QuestionableCompanion.Services;

internal enum RetainerAutoRetainerStartDecision
{
	Start,
	SuppressAlreadyAssigned,
	SuppressInsufficientTokens,
	FailPlansUnavailable,
	FailCharacterDisabled,
	FailRetainersDisabled
}
