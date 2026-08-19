namespace QuestionableCompanion.Services;

public sealed record AutoRetainerMutationResult(bool Success, string Error)
{
	public static AutoRetainerMutationResult Ok { get; } = new AutoRetainerMutationResult(Success: true, string.Empty);

	public static AutoRetainerMutationResult Fail(string error)
	{
		return new AutoRetainerMutationResult(Success: false, error);
	}
}
