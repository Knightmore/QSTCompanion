namespace QuestionableCompanion.Models;

internal enum RetainerBatchPendingAction
{
	None,
	DisableSchedulers,
	Relog,
	NavigateToVocate,
	ReadNativeRoster,
	SaveXadb,
	HireRetainer,
	StartVentureQuest,
	PurchaseStarterGear,
	AssignClassAndGear,
	ConfigureAutoRetainer,
	StartAutoRetainer,
	StopAutoRetainer,
	Cleanup
}
