namespace QuestionableCompanion.Models;

public enum RotationHandoffRecoveryStage
{
	RelogPending,
	WaitingForExactLogin,
	ExactLoginConfirmed,
	PreparingCombatJob,
	CombatJobPrepared,
	QuestStartRequested
}
