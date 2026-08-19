namespace QuestionableCompanion.Services;

internal sealed record AutoRetainerReflectionApplyResult(bool Success, bool Changed, int SaveCalls, string Error, AutoRetainerCharacterSnapshot? Snapshot)
{
	public static AutoRetainerReflectionApplyResult Fail(string error, int saveCalls = 0)
	{
		return new AutoRetainerReflectionApplyResult(Success: false, Changed: false, saveCalls, error, null);
	}
}
