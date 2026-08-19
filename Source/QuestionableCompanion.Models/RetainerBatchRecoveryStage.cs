namespace QuestionableCompanion.Models;

internal enum RetainerBatchRecoveryStage
{
	Created,
	WaitingForDependencies,
	DisablingSchedulers,
	SchedulersDisabled,
	RelogPending,
	WaitingForExactLogin,
	ExactLoginConfirmed,
	WaitingForSafeState,
	ReconcilingRoster,
	ArrivingAtVocate,
	NativeProofBeforeXadb,
	CollectingXadb,
	NativeProofAfterXadb,
	HiringRetainers,
	UnlockingVentures,
	BuyingStarterGear,
	AssigningClassAndGear,
	BootstrappingAutoRetainer,
	CleaningUp,
	Cancelling
}
