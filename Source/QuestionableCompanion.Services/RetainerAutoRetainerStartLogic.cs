namespace QuestionableCompanion.Services;

internal static class RetainerAutoRetainerStartLogic
{
	public static RetainerAutoRetainerStartDecision Decide(bool exactVenturesAlreadyObserved, bool insufficientVentureTokens, bool starterPlansConfigured, bool characterEnabled, bool exactRetainersEnabled)
	{
		if (exactVenturesAlreadyObserved)
		{
			return RetainerAutoRetainerStartDecision.SuppressAlreadyAssigned;
		}
		if (insufficientVentureTokens)
		{
			return RetainerAutoRetainerStartDecision.SuppressInsufficientTokens;
		}
		if (!starterPlansConfigured)
		{
			return RetainerAutoRetainerStartDecision.FailPlansUnavailable;
		}
		if (!characterEnabled)
		{
			return RetainerAutoRetainerStartDecision.FailCharacterDisabled;
		}
		if (!exactRetainersEnabled)
		{
			return RetainerAutoRetainerStartDecision.FailRetainersDisabled;
		}
		return RetainerAutoRetainerStartDecision.Start;
	}
}
