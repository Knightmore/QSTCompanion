using System;

namespace QuestionableCompanion.Services;

internal static class RetainerVocateCleanupLogic
{
	public static bool MustConfirmInputCancellationFirst(bool localizedInputCancellationPromptVisible)
	{
		return localizedInputCancellationPromptVisible;
	}

	public static RetainerVocateCleanupAction DecideInputCancellationAction(bool localizedInputCancellationPromptVisible, bool inputStringPresent, bool cancellationRequested, bool cancellationConfirmed)
	{
		if (localizedInputCancellationPromptVisible)
		{
			if (!cancellationConfirmed)
			{
				return RetainerVocateCleanupAction.ConfirmInputCancellation;
			}
			return RetainerVocateCleanupAction.WaitForInputCancellation;
		}
		if (!inputStringPresent)
		{
			return RetainerVocateCleanupAction.ContinueCleanup;
		}
		if (cancellationConfirmed)
		{
			return RetainerVocateCleanupAction.DirectCloseResidualInputString;
		}
		if (!cancellationRequested)
		{
			return RetainerVocateCleanupAction.RequestInputCancellation;
		}
		return RetainerVocateCleanupAction.WaitForInputCancellation;
	}

	public static bool MatchesInputCancellationPrompt(string actual, string expected)
	{
		string text = NormalizeText(actual);
		string text2 = NormalizeText(expected);
		if (text.Length > 0 && text2.Length > 0)
		{
			return string.Equals(text, text2, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static string NormalizeText(string value)
	{
		return string.Join(' ', (value ?? string.Empty).Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
	}
}
