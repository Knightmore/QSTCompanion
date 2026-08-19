namespace QuestionableCompanion.Services;

internal sealed record RetainerBellMenuReadiness(bool Success, string Error)
{
	internal static RetainerBellMenuReadiness Ready { get; } = new RetainerBellMenuReadiness(Success: true, string.Empty);

	internal static RetainerBellMenuReadiness Fail(string error)
	{
		return new RetainerBellMenuReadiness(Success: false, error);
	}
}
