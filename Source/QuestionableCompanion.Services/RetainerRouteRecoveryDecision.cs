namespace QuestionableCompanion.Services;

public enum RetainerRouteRecoveryDecision
{
	WaitForTransition,
	RecalculateFromCurrentTerritory,
	ContinueCurrentRoute,
	Arrived
}
