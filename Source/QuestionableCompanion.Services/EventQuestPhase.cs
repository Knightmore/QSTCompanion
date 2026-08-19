namespace QuestionableCompanion.Services;

public enum EventQuestPhase
{
	Idle,
	InitializingFirstCharacter,
	WaitingForCharacterLogin,
	CheckingQuestCompletion,
	ResolvingDependencies,
	ExecutingDependencies,
	WaitingForQuestStart,
	QuestActive,
	WaitingBeforeCharacterSwitch,
	Completed,
	Error
}
