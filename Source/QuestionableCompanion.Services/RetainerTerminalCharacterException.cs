using System;

namespace QuestionableCompanion.Services;

public class RetainerTerminalCharacterException : InvalidOperationException
{
	public RetainerTerminalCharacterException(string message)
		: base(message)
	{
	}

	public RetainerTerminalCharacterException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}
}
