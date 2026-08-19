using System.Text.RegularExpressions;

namespace QuestionableCompanion.Services;

public static class QuestIdParser
{
	private static readonly Regex EventQuestPattern = new Regex("^([A-Z])(\\d+)$", RegexOptions.Compiled);

	public static (string rawId, string eventQuestId) ParseQuestId(string questInput)
	{
		if (string.IsNullOrWhiteSpace(questInput))
		{
			return (rawId: questInput, eventQuestId: questInput);
		}
		Match match = EventQuestPattern.Match(questInput);
		if (match.Success)
		{
			_ = match.Groups[1].Value;
			return (rawId: match.Groups[2].Value, eventQuestId: questInput);
		}
		return (rawId: questInput, eventQuestId: questInput);
	}

	public static bool HasEventQuestPrefix(string questId)
	{
		if (string.IsNullOrWhiteSpace(questId))
		{
			return false;
		}
		return EventQuestPattern.IsMatch(questId);
	}

	public static string? GetEventQuestPrefix(string questId)
	{
		if (string.IsNullOrWhiteSpace(questId))
		{
			return null;
		}
		Match match = EventQuestPattern.Match(questId);
		if (!match.Success)
		{
			return null;
		}
		return match.Groups[1].Value;
	}

	public static string GetNumericPart(string questId)
	{
		return ParseQuestId(questId).rawId;
	}

	public static QuestIdType ClassifyQuestId(string questId)
	{
		if (string.IsNullOrWhiteSpace(questId))
		{
			return QuestIdType.Invalid;
		}
		if (HasEventQuestPrefix(questId))
		{
			return QuestIdType.EventQuest;
		}
		if (uint.TryParse(questId, out var _))
		{
			return QuestIdType.Standard;
		}
		return QuestIdType.Unknown;
	}
}
