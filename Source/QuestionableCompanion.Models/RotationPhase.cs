namespace QuestionableCompanion.Models;

public enum RotationPhase
{
	Idle,
	InitializingFirstCharacter,
	WaitingForCharacterLogin,
	ScanningQuests,
	CheckingQuestCompletion,
	ProcessingPostMoogle,
	DCTraveling,
	WaitingForQuestStart,
	Questing,
	InCombat,
	InDungeon,
	HandlingSubmarines,
	SyncingCharacterData,
	WaitingForChauffeur,
	TravellingWithChauffeur,
	QuestActive,
	WaitingForNextCharacterSwitch,
	WaitingBeforeCharacterSwitch,
	WaitingForSafeLocation,
	WaitingForPreCharacterSwitchTasks,
	WaitingForHomeworldReturn,
	Completed,
	Error
}
