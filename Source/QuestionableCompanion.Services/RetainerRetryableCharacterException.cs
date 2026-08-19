using System;

namespace QuestionableCompanion.Services;

public sealed class RetainerRetryableCharacterException : InvalidOperationException
{
	public RetainerRetryableCharacterException(string message)
		: base(message)
	{
	}

	public RetainerRetryableCharacterException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}
}
