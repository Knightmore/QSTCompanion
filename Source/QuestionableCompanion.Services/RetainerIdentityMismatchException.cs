namespace QuestionableCompanion.Services;

public sealed class RetainerIdentityMismatchException : RetainerTerminalCharacterException
{
	public RetainerIdentityMismatchException(string message)
		: base(message)
	{
	}
}
