namespace QuestionableCompanion.Services;

internal static class RetainerBellMenuLogic
{
	internal static RetainerBellMenuDecision Decide(bool ownsRetainerList, bool retainerListReady, bool atSummoningBell, bool individualRetainerWindowReady)
	{
		if (individualRetainerWindowReady)
		{
			return RetainerBellMenuDecision.Block;
		}
		if (ownsRetainerList && retainerListReady && atSummoningBell)
		{
			return RetainerBellMenuDecision.Ready;
		}
		if (retainerListReady)
		{
			return RetainerBellMenuDecision.Block;
		}
		return RetainerBellMenuDecision.Reacquire;
	}
}
