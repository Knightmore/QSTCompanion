namespace QuestionableCompanion.Services;

public enum RotationHandoffResumeAction
{
	WaitForDependencies,
	WaitForDestination,
	WaitForStableWorld,
	ReconstructAtLogin,
	ReconstructAtJobPreparation,
	ReconstructAtQuestStartup,
	ClearStartupConfirmed,
	ClearExpired,
	ClearMalformed,
	ClearIdentityMismatch
}
