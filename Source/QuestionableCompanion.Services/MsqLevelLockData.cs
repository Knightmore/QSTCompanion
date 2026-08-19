namespace QuestionableCompanion.Services;

public sealed class MsqLevelLockData
{
	public required bool IsLevelLocked { get; init; }

	public required int LevelsNeeded { get; init; }

	public required int RequiredLevel { get; init; }

	public required string? QuestName { get; init; }
}
