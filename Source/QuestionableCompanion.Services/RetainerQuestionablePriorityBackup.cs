namespace QuestionableCompanion.Services;

internal sealed record RetainerQuestionablePriorityBackup(string EncodedPriority, bool WasRunning, string PreviousQuestId, string IsolatedQuestId);
