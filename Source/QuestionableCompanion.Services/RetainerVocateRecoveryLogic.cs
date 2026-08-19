namespace QuestionableCompanion.Services;

internal static class RetainerVocateRecoveryLogic
{
	public static bool CanAdoptLoneTalk(bool activeRetainerRecovery, bool exactVocateTarget, bool talkVisible)
	{
		return activeRetainerRecovery && exactVocateTarget && talkVisible;
	}
}
