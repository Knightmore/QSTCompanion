namespace QuestionableCompanion.Models;

public sealed record RetainerCreationSnapshot(bool IsRunning, string CurrentCharacter, string CurrentStage, int CompletedCharacters, int TotalCharacters, string LastMessage, bool CanCancel)
{
	public static RetainerCreationSnapshot Idle { get; } = new RetainerCreationSnapshot(IsRunning: false, string.Empty, "Idle", 0, 0, string.Empty, CanCancel: false);
}
