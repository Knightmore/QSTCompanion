namespace QuestionableCompanion.Models;

public enum HuntLogPhase
{
	Idle,
	Starting,
	SwitchingCharacter,
	WaitingForCharacterLogin,
	RunningCharacter,
	Returning,
	Completed,
	Stopping,
	Error
}
