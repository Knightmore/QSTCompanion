namespace QuestionableCompanion.Services;

public sealed class XadbProgressReadSummary
{
	public int RosterRows { get; init; }

	public int QuestRows { get; init; }

	public int QuestMatchedCharacters { get; init; }

	public int NameFallbackMatches { get; init; }

	public bool QuestDatabaseAvailable { get; init; }

	public int RetainerKnownCharacters { get; init; }

	public int RetainerUnknownCharacters { get; init; }
}
