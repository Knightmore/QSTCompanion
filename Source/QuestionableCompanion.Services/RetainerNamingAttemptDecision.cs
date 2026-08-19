namespace QuestionableCompanion.Services;

internal enum RetainerNamingAttemptDecision
{
	Accepted,
	RetrySameEvent,
	CloseExhaustedSession,
	VerifyExhaustedSessionClosure,
	StructuralFailure
}
