namespace QuestionableCompanion.Services;

internal sealed record AutoRetainerReflectionReadResult(bool Success, string Error, AutoRetainerCharacterSnapshot? Snapshot)
{
	public static AutoRetainerReflectionReadResult Fail(string error)
	{
		return new AutoRetainerReflectionReadResult(Success: false, error, null);
	}
}
