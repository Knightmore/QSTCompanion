namespace QuestionableCompanion.Services;

public sealed record RetainerHireResult(bool Success, bool NameRejected, LiveRetainerInfo? Retainer, string Error)
{
	public static RetainerHireResult Failed(string error)
	{
		return new RetainerHireResult(Success: false, NameRejected: false, null, error);
	}

	public static RetainerHireResult Rejected(string error)
	{
		return new RetainerHireResult(Success: false, NameRejected: true, null, error);
	}
}
